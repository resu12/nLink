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

internal readonly record struct RuntimeUnlockUncommittedTunaFallbackHandoff(
    RuntimeOwnerToken SourceRuntimeOwner,
    RoutePublicationToken SourceRoutePublication,
    RuntimeUnlockAdmissionToken RuntimeUnlockAdmission,
    RuntimeUnlockPendingPreCommitProbeRequest PendingPreCommitProbe,
    RuntimeUnlockRouteCommitSnapshot PreCommitSnapshot,
    int SourceTerminalGeneration,
    long SourceLegGeneration,
    long SourceTransportEpoch,
    int SourceBridgeRecoveryGeneration,
    long SourceDataSessionGeneration,
    long DrainGeneration,
    long CompletedDrainGeneration,
    PostTunaFallbackPeerProofEvidence PeerProofEvidence,
    long ProofTransportEpoch,
    int HandoffGeneration,
    string Reason)
{
    public RegularV4ToTunaRouteTransitionAuthority? RouteTransitionAuthority { get; init; }

    public RegularV4ToTunaPhysicalDrainReceipt? PhysicalDrainReceipt { get; init; }
}

internal enum RuntimeUnlockUncommittedTunaFallbackHandoffTransitionStatus
{
    Prepared,
    Committed,
    Idempotent,
    Rejected,
}

internal readonly record struct RuntimeUnlockUncommittedTunaFallbackHandoffTransition(
    FileTransferCoordinatorEvent CoordinatorEvent,
    RuntimeUnlockUncommittedTunaFallbackHandoff Handoff,
    RegularV4FrontierRepairPressureBinding? PressureBinding,
    FileTransferRegularV4FrontierRepairSettlement? PressureSettlement,
    FileTransferRegularV4FrontierRepairPressureSettlementCommand? PressureSettlementCommand);

internal readonly record struct RuntimeUnlockUncommittedTunaFallbackHandoffTransitionDecision(
    RuntimeUnlockUncommittedTunaFallbackHandoffTransitionStatus Status,
    string Reason,
    RuntimeUnlockUncommittedTunaFallbackHandoffTransition? Transition,
    FileTransferCoordinatorDecision? RouteDecision)
{
    public FileTransferCoordinatorState State { get; init; } = null!;
}

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
    string DecisionReason,
    int? FrontierLagChunks = null);

internal enum RegularV4FrontierRepairPressureBindingState
{
    Pending = 1,
    Installed = 2,
    RetainedPredecessor = 3,
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
    FileTransferRegularV4FrontierRepairPressureInstallCommand? InstallCommand,
    FileTransferRegularV4FrontierRepairPressureSettlementCommand? SettlementCommand)
{
    public FileTransferCoordinatorState State { get; init; } = null!;
}

internal readonly record struct RegularV4FrontierRepairPressureSubstate(
    RegularV4FrontierRepairPressureBinding? PendingBinding = null,
    FileTransferRegularV4FrontierRepairPressureInstallCommand? PendingInstallCommand = null,
    RegularV4FrontierRepairPressureBinding? InstalledBinding = null,
    FileTransferRegularV4FrontierRepairPressureInstallCommand? InstalledInstallCommand = null,
    FileTransferRegularV4FrontierRepairPressureInstallReceipt? InstalledInstallReceipt = null,
    RegularV4FrontierRepairPressureBinding? RetainedPredecessorBinding = null,
    FileTransferRegularV4FrontierRepairPressureInstallCommand? RetainedPredecessorInstallCommand = null,
    FileTransferRegularV4FrontierRepairPressureInstallReceipt? RetainedPredecessorInstallReceipt = null,
    FileTransferRegularV4FrontierRepairSettlement? LastSettlement = null,
    long LastInstallGeneration = 0,
    FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt? LastSettlementApplyReceipt = null,
    RegularV4FrontierRepairPressureRecoveryHandoffTransition? LastRecoveryHandoff = null)
{
    internal static RegularV4FrontierRepairPressureSubstate Empty => default;

    internal bool HasBinding =>
        PendingBinding is not null ||
        InstalledBinding is not null ||
        RetainedPredecessorBinding is not null;
}

internal enum FileTransferRouteTransitionKind
{
    RegularV4ToTunaV4 = 1,
    TunaV4ToPostTunaFallbackV6 = 2,
    PostTunaFallbackV6ToTunaV4 = 3,
    UncommittedTunaToPostTunaFallbackV6 = 4,
}

internal enum FileTransferRouteTransitionState
{
    Prepared = 1,
    Draining = 2,
    Committed = 3,
    Invalidated = 4,
}

internal abstract record FileTransferRouteTransitionProofBoundary
{
    public abstract bool IsDefault { get; }
}

internal sealed record FileTransferTunaActivationTransitionProofBoundary(
    RuntimeUnlockAdmissionToken RuntimeUnlockAdmission,
    RuntimeUnlockPendingPreCommitProbeRequest PendingPreCommitProbe,
    RuntimeUnlockRouteCommitProof AcceptedLaunchBoundary,
    long LaunchGeneration,
    RuntimeUnlockRetryDispatchInstallReceipt? EffectiveRetryInstallReceipt = null,
    FallbackTailYieldDecision? FallbackTailYieldDecision = null)
    : FileTransferRouteTransitionProofBoundary
{
    public override bool IsDefault =>
        RuntimeUnlockAdmission.IsDefault ||
        PendingPreCommitProbe.IsDefault ||
        AcceptedLaunchBoundary.AdmissionToken != RuntimeUnlockAdmission ||
        LaunchGeneration <= 0 ||
        (EffectiveRetryInstallReceipt is { } receipt && receipt.IsDefault) ||
        (FallbackTailYieldDecision is { } yield && yield.IsDefault);
}

internal sealed record FileTransferFallbackTransitionProofBoundary(
    RouteCommitToken? SourceRouteCommit,
    PostTunaFallbackPeerProofEvidence PeerProofEvidence,
    PostTunaFallbackPeerProofFrameIdentity RetainedFrameIdentity,
    long TargetTransportEpoch,
    FileTransferAuthorityId? PredecessorTransitionAuthorityId = null)
    : FileTransferRouteTransitionProofBoundary
{
    public override bool IsDefault =>
        PeerProofEvidence is not { IsComplete: true, IsExactFrameBound: true } ||
        RetainedFrameIdentity is not { IsComplete: true } ||
        PeerProofEvidence.FrameIdentity != RetainedFrameIdentity ||
        TargetTransportEpoch <= 0 ||
        (SourceRouteCommit is null &&
         (PredecessorTransitionAuthorityId is null ||
          PredecessorTransitionAuthorityId.Value.IsDefault));
}

internal readonly record struct FileTransferRouteTransitionDurableCheckpoint(
    RuntimeOwnerToken SourceRuntimeOwner,
    RoutePublicationToken SourceRoutePublication,
    int SourceTerminalGeneration,
    int SourceLegGeneration,
    long SourceTransportEpoch,
    int SourceBridgeRecoveryGeneration,
    long DataSessionGeneration,
    int StateEpoch,
    int ContiguousCommittedChunkIndex,
    int DurableHighestChunkIndex,
    int ChunkCount,
    int CheckpointGeneration)
{
    public bool IsDefault =>
        SourceRuntimeOwner.IsDefault ||
        SourceRoutePublication.IsDefault ||
        SourceLegGeneration <= 0 ||
        SourceTransportEpoch < 0 ||
        SourceBridgeRecoveryGeneration < 0 ||
        DataSessionGeneration <= 0 ||
        StateEpoch < 0 ||
        ContiguousCommittedChunkIndex < 0 ||
        DurableHighestChunkIndex < ContiguousCommittedChunkIndex - 1 ||
        ChunkCount < 0 ||
        ContiguousCommittedChunkIndex > ChunkCount ||
        DurableHighestChunkIndex >= ChunkCount ||
        CheckpointGeneration <= 0;
}

internal readonly record struct FileTransferRouteTransitionPrepareRequest(
    FileTransferRouteTransitionKind Kind,
    RuntimeOwnerToken SourceRuntimeOwner,
    RoutePublicationToken SourceRoutePublication,
    RouteCommitToken? SourceRouteCommit,
    FileTransferRoute SourceRoute,
    int SourceProtocolVersion,
    FileTransferTransportKind SourceTransport,
    FileTransferRouteTransitionProofBoundary ProofBoundary,
    FileTransferRouteTransitionDurableCheckpoint DurableCheckpoint,
    FileTransferRoute TargetRoute,
    int TargetProtocolVersion,
    FileTransferTransportKind TargetTransport,
    FileTransferTransportHandoffKind HandoffKind,
    FileTransferAuthorityId? PredecessorTransitionAuthorityId,
    string Reason);

internal readonly record struct FileTransferRouteTransitionAuthority(
    FileTransferRouteTransitionKind Kind,
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    RuntimeOwnerToken SourceRuntimeOwner,
    RoutePublicationToken SourceRoutePublication,
    RouteCommitToken? SourceRouteCommit,
    FileTransferRoute SourceRoute,
    int SourceProtocolVersion,
    FileTransferTransportKind SourceTransport,
    int SourceTerminalGeneration,
    int SourceLiveRouteEpoch,
    int SourceLegGeneration,
    long SourceTransportEpoch,
    int SourceBridgeRecoveryGeneration,
    long DataSessionGeneration,
    FileTransferRouteTransitionProofBoundary ProofBoundary,
    FileTransferRouteTransitionDurableCheckpoint DurableCheckpoint,
    FileTransferRoute TargetRoute,
    int TargetProtocolVersion,
    FileTransferTransportKind TargetTransport,
    FileTransferTransportHandoffKind HandoffKind,
    long TransitionGeneration,
    FileTransferAuthorityId AuthorityId,
    FileTransferAuthorityId? PredecessorTransitionAuthorityId,
    string Reason)
{
    public bool IsDefault =>
        string.IsNullOrWhiteSpace(SessionId) ||
        string.IsNullOrWhiteSpace(TransferId) ||
        SourceRuntimeOwner.IsDefault ||
        SourceRoutePublication.IsDefault ||
        SourceLegGeneration <= 0 ||
        SourceTransportEpoch < 0 ||
        SourceBridgeRecoveryGeneration < 0 ||
        DataSessionGeneration <= 0 ||
        ProofBoundary is null ||
        ProofBoundary.IsDefault ||
        DurableCheckpoint.IsDefault ||
        TransitionGeneration <= 0 ||
        AuthorityId.IsDefault ||
        string.IsNullOrWhiteSpace(Reason);
}

internal readonly record struct FileTransferRouteTransitionPauseBindCommand(
    FileTransferRouteTransitionAuthority Authority);

internal enum FileTransferRouteTransitionPauseBindStatus
{
    Installed,
    Idempotent,
    Rejected,
}

internal readonly record struct FileTransferRouteTransitionPauseBindReceipt(
    FileTransferRouteTransitionPauseBindCommand Command,
    FileTransferRouteTransitionPauseBindStatus Status,
    string Reason);

internal readonly record struct FileTransferRouteTransitionPhysicalDrainCommand(
    FileTransferRouteTransitionAuthority Authority,
    long CapturedThroughPhysicalSendGeneration,
    IReadOnlyList<long> CapturedPhysicalSendLeaseIds,
    int CapturedPhysicalSendCount,
    string CapturedPhysicalSendFingerprint);

internal enum FileTransferRouteTransitionPhysicalDrainOutcome
{
    Completed,
}

internal readonly record struct FileTransferRouteTransitionPhysicalDrainReceipt(
    FileTransferRouteTransitionPhysicalDrainCommand Command,
    IReadOnlyList<long> CompletedPhysicalSendLeaseIds,
    int CompletedPhysicalSendCount,
    string CompletedPhysicalSendFingerprint,
    FileTransferRouteTransitionPhysicalDrainOutcome Outcome,
    string Reason);

internal enum FileTransferRouteTransitionDispositionKind
{
    Committed,
    RolledBack,
    TerminalInvalidated,
    Superseded,
}

internal readonly record struct FileTransferRouteTransitionDispositionCommand(
    FileTransferRouteTransitionAuthority Authority,
    FileTransferRouteTransitionDispositionKind Kind,
    RouteCommitToken? RouteCommitToken,
    RoutePublicationToken? RoutePublicationToken,
    string Reason)
{
    public RuntimeUnlockRouteCommitProof? FinalProof { get; init; }
}

internal enum FileTransferRouteTransitionDispositionApplyStatus
{
    Applied,
    Idempotent,
    Rejected,
}

internal readonly record struct FileTransferRouteTransitionDispositionReceipt(
    FileTransferRouteTransitionDispositionCommand Command,
    FileTransferRouteTransitionDispositionApplyStatus Status,
    string Reason);

internal readonly record struct FileTransferRouteTransitionOperation(
    FileTransferRouteTransitionAuthority Authority,
    FileTransferRouteTransitionState State,
    FileTransferRouteTransitionPauseBindReceipt? PauseBindReceipt = null,
    FileTransferRouteTransitionPhysicalDrainCommand? DrainCommand = null,
    FileTransferRouteTransitionPhysicalDrainReceipt? DrainReceipt = null,
    RuntimeUnlockRouteCommitProof? FinalProof = null,
    FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt?
        PressureSettlementReceipt = null,
    RouteCommitToken? RouteCommitToken = null,
    RoutePublicationToken? RoutePublicationToken = null,
    FileTransferRouteTransitionDispositionCommand? DispositionCommand = null,
    FileTransferRouteTransitionDispositionReceipt? DispositionReceipt = null);

internal readonly record struct FileTransferRouteTransitionSupersession(
    FileTransferRouteTransitionAuthority PredecessorAuthority,
    FileTransferRouteTransitionAuthority SuccessorAuthority,
    FileTransferRouteTransitionPauseBindCommand PauseRebindCommand,
    FileTransferRouteTransitionPauseBindReceipt? PauseRebindReceipt = null);

internal readonly record struct FileTransferRouteTransitionSubstate(
    FileTransferRouteTransitionOperation? CurrentOperation = null,
    FileTransferRouteTransitionSupersession? PendingSupersession = null,
    FileTransferRouteTransitionOperation? LastTerminalOperation = null,
    long LastTransitionGeneration = 0)
{
    internal static FileTransferRouteTransitionSubstate Empty => default;
}

internal enum FileTransferRouteTransitionDecisionStatus
{
    Prepared,
    Draining,
    Committed,
    Invalidated,
    SupersessionStaged,
    Idempotent,
    Rejected,
}

internal readonly record struct FileTransferRouteTransitionDecision(
    FileTransferRouteTransitionDecisionStatus Status,
    string Reason,
    FileTransferRouteTransitionAuthority? Authority = null,
    FileTransferRouteTransitionPauseBindCommand? PauseBindCommand = null,
    FileTransferRouteTransitionPhysicalDrainCommand? DrainCommand = null,
    FileTransferRouteTransitionDispositionCommand? DispositionCommand = null,
    FileTransferCoordinatorDecision? RouteDecision = null)
{
    public FileTransferCoordinatorState State { get; init; } = null!;
}

internal enum RegularV4ToTunaRouteTransitionState
{
    Prepared = 1,
    Draining = 2,
    Committed = 3,
    Invalidated = 4,
}

internal readonly record struct RegularV4ToTunaDurableCheckpoint(
    RuntimeOwnerToken SourceRuntimeOwner,
    RoutePublicationToken SourceRoutePublication,
    int SourceTerminalGeneration,
    int SourceLegGeneration,
    long SourceTransportEpoch,
    int SourceBridgeRecoveryGeneration,
    long DataSessionGeneration,
    int StateEpoch,
    int ContiguousCommittedChunkIndex,
    int DurableHighestChunkIndex,
    int ChunkCount,
    int CheckpointGeneration)
{
    public bool IsDefault =>
        SourceRuntimeOwner.IsDefault ||
        SourceRoutePublication.IsDefault ||
        SourceLegGeneration <= 0 ||
        SourceTransportEpoch < 0 ||
        SourceBridgeRecoveryGeneration < 0 ||
        DataSessionGeneration <= 0 ||
        StateEpoch < 0 ||
        ContiguousCommittedChunkIndex < 0 ||
        DurableHighestChunkIndex < ContiguousCommittedChunkIndex - 1 ||
        ChunkCount < 0 ||
        ContiguousCommittedChunkIndex > ChunkCount ||
        DurableHighestChunkIndex >= ChunkCount ||
        CheckpointGeneration <= 0;
}

internal readonly record struct RegularV4ToTunaRouteTransitionPrepareRequest(
    RuntimeOwnerToken SourceRuntimeOwner,
    RoutePublicationToken SourceRoutePublication,
    RuntimeUnlockAdmissionToken RuntimeUnlockAdmission,
    RuntimeUnlockPendingPreCommitProbeRequest PendingPreCommitProbe,
    RuntimeUnlockRouteCommitProof AcceptedLaunchBoundary,
    long LaunchGeneration,
    RegularV4ToTunaDurableCheckpoint DurableCheckpoint,
    string Reason);

internal readonly record struct RegularV4ToTunaRouteTransitionAuthority(
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    RuntimeOwnerToken SourceRuntimeOwner,
    RoutePublicationToken SourceRoutePublication,
    int SourceTerminalGeneration,
    int SourceLiveRouteEpoch,
    int SourceLegGeneration,
    long SourceTransportEpoch,
    int SourceBridgeRecoveryGeneration,
    long DataSessionGeneration,
    RuntimeUnlockAdmissionToken RuntimeUnlockAdmission,
    RuntimeUnlockPendingPreCommitProbeRequest PendingPreCommitProbe,
    RuntimeUnlockRouteCommitProof AcceptedLaunchBoundary,
    long LaunchGeneration,
    RegularV4ToTunaDurableCheckpoint DurableCheckpoint,
    FileTransferRoute TargetRoute,
    int TargetProtocolVersion,
    FileTransferTransportKind TargetTransport,
    FileTransferTransportHandoffKind HandoffKind,
    long TransitionGeneration,
    FileTransferAuthorityId AuthorityId,
    string Reason)
{
    public bool IsDefault =>
        string.IsNullOrWhiteSpace(SessionId) ||
        string.IsNullOrWhiteSpace(TransferId) ||
        SourceRuntimeOwner.IsDefault ||
        SourceRoutePublication.IsDefault ||
        RuntimeUnlockAdmission.IsDefault ||
        PendingPreCommitProbe.IsDefault ||
        DurableCheckpoint.IsDefault ||
        AuthorityId.IsDefault ||
        AcceptedLaunchBoundary.AdmissionToken != RuntimeUnlockAdmission ||
        AcceptedLaunchBoundary.TransactionGeneration <= 0 ||
        AcceptedLaunchBoundary.OfferGeneration <= 0 ||
        AcceptedLaunchBoundary.TunaPathLeaseGeneration <= 0 ||
        AcceptedLaunchBoundary.TargetRoute != FileTransferRoute.FileTunaV4 ||
        AcceptedLaunchBoundary.ProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
        AcceptedLaunchBoundary.TargetTransport != FileTransferTransportKind.Tuna ||
        AcceptedLaunchBoundary.HandoffKind !=
            FileTransferTransportHandoffKind.NormalToTunaActivation ||
        SourceLegGeneration <= 0 ||
        SourceTransportEpoch < 0 ||
        SourceBridgeRecoveryGeneration < 0 ||
        DataSessionGeneration <= 0 ||
        LaunchGeneration <= 0 ||
        TargetRoute != FileTransferRoute.FileTunaV4 ||
        TargetProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
        TargetTransport != FileTransferTransportKind.Tuna ||
        HandoffKind != FileTransferTransportHandoffKind.NormalToTunaActivation ||
        TransitionGeneration <= 0;
}

internal readonly record struct RegularV4ToTunaActivationPauseBindCommand(
    RegularV4ToTunaRouteTransitionAuthority Authority);

internal enum RegularV4ToTunaActivationPauseBindStatus
{
    Installed,
    Idempotent,
    Rejected,
}

internal readonly record struct RegularV4ToTunaActivationPauseBindReceipt(
    RegularV4ToTunaActivationPauseBindCommand Command,
    RegularV4ToTunaActivationPauseBindStatus Status,
    string Reason);

internal readonly record struct RegularV4ToTunaPhysicalDrainCommand(
    RegularV4ToTunaRouteTransitionAuthority Authority,
    long CapturedThroughPhysicalSendGeneration,
    IReadOnlyList<long> CapturedPhysicalSendLeaseIds,
    int CapturedPhysicalSendCount,
    string CapturedPhysicalSendFingerprint);

internal enum RegularV4ToTunaPhysicalDrainOutcome
{
    Completed,
}

internal readonly record struct RegularV4ToTunaPhysicalDrainReceipt(
    RegularV4ToTunaPhysicalDrainCommand Command,
    IReadOnlyList<long> CompletedPhysicalSendLeaseIds,
    int CompletedPhysicalSendCount,
    string CompletedPhysicalSendFingerprint,
    RegularV4ToTunaPhysicalDrainOutcome Outcome,
    string Reason);

internal enum RegularV4ToTunaRouteTransitionDispositionKind
{
    Committed,
    RolledBack,
    TerminalInvalidated,
    Superseded,
}

internal readonly record struct RegularV4ToTunaRouteTransitionDispositionCommand(
    RegularV4ToTunaRouteTransitionAuthority Authority,
    RegularV4ToTunaRouteTransitionDispositionKind Kind,
    RouteCommitToken? RouteCommitToken,
    RoutePublicationToken? RoutePublicationToken,
    string Reason)
{
    public RuntimeUnlockRouteCommitProof? FinalProof { get; init; }
}

internal enum RegularV4ToTunaRouteTransitionDispositionApplyStatus
{
    Applied,
    Idempotent,
    Rejected,
}

internal readonly record struct RegularV4ToTunaRouteTransitionDispositionReceipt(
    RegularV4ToTunaRouteTransitionDispositionCommand Command,
    RegularV4ToTunaRouteTransitionDispositionApplyStatus Status,
    string Reason);

internal readonly record struct RegularV4ToTunaRouteTransitionOperation(
    RegularV4ToTunaRouteTransitionAuthority Authority,
    RegularV4ToTunaRouteTransitionState State,
    RegularV4ToTunaActivationPauseBindReceipt? PauseBindReceipt = null,
    RegularV4ToTunaPhysicalDrainCommand? DrainCommand = null,
    RegularV4ToTunaPhysicalDrainReceipt? DrainReceipt = null,
    RuntimeUnlockRouteCommitProof? FinalProof = null,
    FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt?
        PressureSettlementReceipt = null,
    RouteCommitToken? RouteCommitToken = null,
    RoutePublicationToken? RoutePublicationToken = null,
    RegularV4ToTunaRouteTransitionDispositionCommand? DispositionCommand = null,
    RegularV4ToTunaRouteTransitionDispositionReceipt? DispositionReceipt = null);

internal readonly record struct RegularV4ToTunaRouteTransitionSubstate(
    RegularV4ToTunaRouteTransitionOperation? CurrentOperation = null,
    RegularV4ToTunaRouteTransitionOperation? LastTerminalOperation = null,
    long LastTransitionGeneration = 0)
{
    internal static RegularV4ToTunaRouteTransitionSubstate Empty => default;
}

internal readonly record struct FileTransferCoordinatorPersistentState(
    RegularV4FrontierRepairPressureSubstate RegularV4FrontierRepairPressure = default,
    FileTransferRouteTransitionSubstate FileTransferRouteTransition = default)
{
    internal static FileTransferCoordinatorPersistentState Empty => default;

    // Compatibility projection for the Slice-3 Core and Infra surfaces. The
    // persistent value is the generic transition aggregate; callers cannot
    // establish a second route-transition authority through this projection.
    internal RegularV4ToTunaRouteTransitionSubstate RegularV4ToTunaRouteTransition
    {
        get => FileTransferRouteTransitionCompatibility.ToRegularV4ToTuna(
            FileTransferRouteTransition);
        init => FileTransferRouteTransition =
            FileTransferRouteTransitionCompatibility.FromRegularV4ToTuna(value);
    }
}

internal enum RegularV4ToTunaRouteTransitionDecisionStatus
{
    Prepared,
    Draining,
    Committed,
    Invalidated,
    Idempotent,
    Rejected,
}

internal readonly record struct RegularV4ToTunaRouteTransitionDecision(
    RegularV4ToTunaRouteTransitionDecisionStatus Status,
    string Reason,
    RegularV4ToTunaRouteTransitionAuthority? Authority = null,
    RegularV4ToTunaActivationPauseBindCommand? PauseBindCommand = null,
    RegularV4ToTunaPhysicalDrainCommand? DrainCommand = null,
    RegularV4ToTunaRouteTransitionDispositionCommand? DispositionCommand = null,
    FileTransferCoordinatorDecision? RouteDecision = null)
{
    public FileTransferCoordinatorState State { get; init; } = null!;
}

internal static class FileTransferRouteTransitionCompatibility
{
    internal static FileTransferRouteTransitionSubstate FromRegularV4ToTuna(
        RegularV4ToTunaRouteTransitionSubstate source)
        => new(
            source.CurrentOperation is { } current
                ? FromRegularV4ToTuna(current)
                : null,
            PendingSupersession: null,
            source.LastTerminalOperation is { } terminal
                ? FromRegularV4ToTuna(terminal)
                : null,
            source.LastTransitionGeneration);

    internal static RegularV4ToTunaRouteTransitionSubstate ToRegularV4ToTuna(
        FileTransferRouteTransitionSubstate source)
        => new(
            source.CurrentOperation is { } current &&
            current.Authority.Kind == FileTransferRouteTransitionKind.RegularV4ToTunaV4
                ? ToRegularV4ToTuna(current)
                : null,
            source.LastTerminalOperation is { } terminal &&
            terminal.Authority.Kind == FileTransferRouteTransitionKind.RegularV4ToTunaV4
                ? ToRegularV4ToTuna(terminal)
                : null,
            source.LastTransitionGeneration);

    internal static FileTransferRouteTransitionAuthority FromRegularV4ToTuna(
        RegularV4ToTunaRouteTransitionAuthority source)
        => new(
            FileTransferRouteTransitionKind.RegularV4ToTunaV4,
            source.SessionId,
            source.TransferId,
            source.Direction,
            source.SourceRuntimeOwner,
            source.SourceRoutePublication,
            SourceRouteCommit: null,
            FileTransferRoute.RegularNknV4Fast,
            FileTransferProtocol.ProtocolVersionV4,
            FileTransferTransportKind.RegularNkn,
            source.SourceTerminalGeneration,
            source.SourceLiveRouteEpoch,
            source.SourceLegGeneration,
            source.SourceTransportEpoch,
            source.SourceBridgeRecoveryGeneration,
            source.DataSessionGeneration,
            new FileTransferTunaActivationTransitionProofBoundary(
                source.RuntimeUnlockAdmission,
                source.PendingPreCommitProbe,
                source.AcceptedLaunchBoundary,
                source.LaunchGeneration),
            FromRegularV4ToTuna(source.DurableCheckpoint),
            source.TargetRoute,
            source.TargetProtocolVersion,
            source.TargetTransport,
            source.HandoffKind,
            source.TransitionGeneration,
            source.AuthorityId,
            PredecessorTransitionAuthorityId: null,
            source.Reason);

    internal static RegularV4ToTunaRouteTransitionAuthority ToRegularV4ToTuna(
        FileTransferRouteTransitionAuthority source)
    {
        if (source.Kind != FileTransferRouteTransitionKind.RegularV4ToTunaV4 ||
            source.ProofBoundary is not FileTransferTunaActivationTransitionProofBoundary proof)
        {
            throw new InvalidOperationException(
                "The generic route transition is not a regular-V4-to-Tuna transition.");
        }

        return new RegularV4ToTunaRouteTransitionAuthority(
            source.SessionId,
            source.TransferId,
            source.Direction,
            source.SourceRuntimeOwner,
            source.SourceRoutePublication,
            source.SourceTerminalGeneration,
            source.SourceLiveRouteEpoch,
            source.SourceLegGeneration,
            source.SourceTransportEpoch,
            source.SourceBridgeRecoveryGeneration,
            source.DataSessionGeneration,
            proof.RuntimeUnlockAdmission,
            proof.PendingPreCommitProbe,
            proof.AcceptedLaunchBoundary,
            proof.LaunchGeneration,
            ToRegularV4ToTuna(source.DurableCheckpoint),
            source.TargetRoute,
            source.TargetProtocolVersion,
            source.TargetTransport,
            source.HandoffKind,
            source.TransitionGeneration,
            source.AuthorityId,
            source.Reason);
    }

    internal static FileTransferRouteTransitionDurableCheckpoint FromRegularV4ToTuna(
        RegularV4ToTunaDurableCheckpoint source)
        => new(
            source.SourceRuntimeOwner,
            source.SourceRoutePublication,
            source.SourceTerminalGeneration,
            source.SourceLegGeneration,
            source.SourceTransportEpoch,
            source.SourceBridgeRecoveryGeneration,
            source.DataSessionGeneration,
            source.StateEpoch,
            source.ContiguousCommittedChunkIndex,
            source.DurableHighestChunkIndex,
            source.ChunkCount,
            source.CheckpointGeneration);

    internal static RegularV4ToTunaDurableCheckpoint ToRegularV4ToTuna(
        FileTransferRouteTransitionDurableCheckpoint source)
        => new(
            source.SourceRuntimeOwner,
            source.SourceRoutePublication,
            source.SourceTerminalGeneration,
            source.SourceLegGeneration,
            source.SourceTransportEpoch,
            source.SourceBridgeRecoveryGeneration,
            source.DataSessionGeneration,
            source.StateEpoch,
            source.ContiguousCommittedChunkIndex,
            source.DurableHighestChunkIndex,
            source.ChunkCount,
            source.CheckpointGeneration);

    private static FileTransferRouteTransitionOperation FromRegularV4ToTuna(
        RegularV4ToTunaRouteTransitionOperation source)
        => new(
            FromRegularV4ToTuna(source.Authority),
            (FileTransferRouteTransitionState)(int)source.State,
            source.PauseBindReceipt is { } pause
                ? new FileTransferRouteTransitionPauseBindReceipt(
                    new FileTransferRouteTransitionPauseBindCommand(
                        FromRegularV4ToTuna(pause.Command.Authority)),
                    (FileTransferRouteTransitionPauseBindStatus)(int)pause.Status,
                    pause.Reason)
                : null,
            source.DrainCommand is { } drain
                ? FromRegularV4ToTuna(drain)
                : null,
            source.DrainReceipt is { } drainReceipt
                ? FromRegularV4ToTuna(drainReceipt)
                : null,
            source.FinalProof,
            source.PressureSettlementReceipt,
            source.RouteCommitToken,
            source.RoutePublicationToken,
            source.DispositionCommand is { } disposition
                ? FromRegularV4ToTuna(disposition)
                : null,
            source.DispositionReceipt is { } dispositionReceipt
                ? FromRegularV4ToTuna(dispositionReceipt)
                : null);

    private static RegularV4ToTunaRouteTransitionOperation ToRegularV4ToTuna(
        FileTransferRouteTransitionOperation source)
        => new(
            ToRegularV4ToTuna(source.Authority),
            (RegularV4ToTunaRouteTransitionState)(int)source.State,
            source.PauseBindReceipt is { } pause
                ? new RegularV4ToTunaActivationPauseBindReceipt(
                    new RegularV4ToTunaActivationPauseBindCommand(
                        ToRegularV4ToTuna(pause.Command.Authority)),
                    (RegularV4ToTunaActivationPauseBindStatus)(int)pause.Status,
                    pause.Reason)
                : null,
            source.DrainCommand is { } drain
                ? ToRegularV4ToTuna(drain)
                : null,
            source.DrainReceipt is { } drainReceipt
                ? ToRegularV4ToTuna(drainReceipt)
                : null,
            source.FinalProof,
            source.PressureSettlementReceipt,
            source.RouteCommitToken,
            source.RoutePublicationToken,
            source.DispositionCommand is { } disposition
                ? ToRegularV4ToTuna(disposition)
                : null,
            source.DispositionReceipt is { } dispositionReceipt
                ? ToRegularV4ToTuna(dispositionReceipt)
                : null);

    private static FileTransferRouteTransitionPhysicalDrainCommand FromRegularV4ToTuna(
        RegularV4ToTunaPhysicalDrainCommand source)
        => new(
            FromRegularV4ToTuna(source.Authority),
            source.CapturedThroughPhysicalSendGeneration,
            source.CapturedPhysicalSendLeaseIds,
            source.CapturedPhysicalSendCount,
            source.CapturedPhysicalSendFingerprint);

    private static RegularV4ToTunaPhysicalDrainCommand ToRegularV4ToTuna(
        FileTransferRouteTransitionPhysicalDrainCommand source)
        => new(
            ToRegularV4ToTuna(source.Authority),
            source.CapturedThroughPhysicalSendGeneration,
            source.CapturedPhysicalSendLeaseIds,
            source.CapturedPhysicalSendCount,
            source.CapturedPhysicalSendFingerprint);

    private static FileTransferRouteTransitionPhysicalDrainReceipt FromRegularV4ToTuna(
        RegularV4ToTunaPhysicalDrainReceipt source)
        => new(
            FromRegularV4ToTuna(source.Command),
            source.CompletedPhysicalSendLeaseIds,
            source.CompletedPhysicalSendCount,
            source.CompletedPhysicalSendFingerprint,
            (FileTransferRouteTransitionPhysicalDrainOutcome)(int)source.Outcome,
            source.Reason);

    private static RegularV4ToTunaPhysicalDrainReceipt ToRegularV4ToTuna(
        FileTransferRouteTransitionPhysicalDrainReceipt source)
        => new(
            ToRegularV4ToTuna(source.Command),
            source.CompletedPhysicalSendLeaseIds,
            source.CompletedPhysicalSendCount,
            source.CompletedPhysicalSendFingerprint,
            (RegularV4ToTunaPhysicalDrainOutcome)(int)source.Outcome,
            source.Reason);

    private static FileTransferRouteTransitionDispositionCommand FromRegularV4ToTuna(
        RegularV4ToTunaRouteTransitionDispositionCommand source)
        => new(
            FromRegularV4ToTuna(source.Authority),
            (FileTransferRouteTransitionDispositionKind)(int)source.Kind,
            source.RouteCommitToken,
            source.RoutePublicationToken,
            source.Reason)
        {
            FinalProof = source.FinalProof,
        };

    private static RegularV4ToTunaRouteTransitionDispositionCommand ToRegularV4ToTuna(
        FileTransferRouteTransitionDispositionCommand source)
        => new(
            ToRegularV4ToTuna(source.Authority),
            (RegularV4ToTunaRouteTransitionDispositionKind)(int)source.Kind,
            source.RouteCommitToken,
            source.RoutePublicationToken,
            source.Reason)
        {
            FinalProof = source.FinalProof,
        };

    private static FileTransferRouteTransitionDispositionReceipt FromRegularV4ToTuna(
        RegularV4ToTunaRouteTransitionDispositionReceipt source)
        => new(
            FromRegularV4ToTuna(source.Command),
            (FileTransferRouteTransitionDispositionApplyStatus)(int)source.Status,
            source.Reason);

    private static RegularV4ToTunaRouteTransitionDispositionReceipt ToRegularV4ToTuna(
        FileTransferRouteTransitionDispositionReceipt source)
        => new(
            ToRegularV4ToTuna(source.Command),
            (RegularV4ToTunaRouteTransitionDispositionApplyStatus)(int)source.Status,
            source.Reason);
}

internal enum RegularV4FrontierRepairPressureRecoveryHandoffStatus
{
    Prepared,
    Committed,
    Idempotent,
    Rejected,
}

internal readonly record struct RegularV4FrontierRepairPressureRecoveryHandoffTransition(
    RegularV4FrontierRepairPressureBinding PressureBinding,
    FileTransferRegularV4FrontierRepairSettlement Settlement,
    FileTransferRegularV4FrontierRepairPressureSettlementCommand SettlementCommand,
    FileTransferCoordinatorActionRequest RecoveryRequest,
    BridgeRecoveryDecision BridgeRecoveryDecision,
    RecoveryLivenessLease RecoveryLivenessLease,
    FileTransferBridgeRecoveryExecutionCommand BridgeRecoveryExecutionCommand);

internal readonly record struct RegularV4FrontierRepairPressureRecoveryHandoffDecision(
    RegularV4FrontierRepairPressureRecoveryHandoffStatus Status,
    string Reason,
    RegularV4FrontierRepairPressureRecoveryHandoffTransition? Transition)
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
    FileTransferCoordinatorPersistentState PersistentState = default,
    RuntimeUnlockUncommittedTunaFallbackHandoffTransition?
        LastRuntimeUnlockUncommittedTunaFallbackHandoff = null,
    FileTransferBridgeRecoveryExecutionCommand?
        CurrentBridgeRecoveryExecutionCommand = null,
    FileTransferBridgeRecoveryExecutionState?
        CurrentBridgeRecoveryExecutionState = null,
    FileTransferBridgeRecoveryExecutionReceipt?
        LastBridgeRecoveryExecutionReceipt = null)
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
        FallbackTailYieldTransportEpochReauthorization = null,
    RuntimeUnlockUncommittedTunaFallbackHandoff?
        RuntimeUnlockUncommittedTunaFallbackHandoff = null);

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
    FileTransferBridgeRecoveryExecutionCommand? BridgeRecoveryExecutionCommand = null,
    RoutePublicationDecision? RoutePublicationDecision = null,
    RoutePublicationToken? RoutePublicationToken = null,
    RuntimeUnlockPendingPreCommitProbeDecision? RuntimeUnlockPendingPreCommitProbeDecision = null,
    RuntimeUnlockPendingPreCommitProbeRequest? RuntimeUnlockPendingPreCommitProbeRequest = null)
{
    public FileTransferCoordinatorState State { get; init; } = null!;
}

internal enum FileTransferBridgeRecoveryExecutionDecisionStatus
{
    Accepted,
    Idempotent,
    Rejected,
}

internal readonly record struct FileTransferBridgeRecoveryExecutionDecision(
    FileTransferBridgeRecoveryExecutionDecisionStatus Status,
    string Reason,
    FileTransferBridgeRecoveryExecutionCommand Command)
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

    internal static FileTransferRouteTransitionSubstate
        GetFileTransferRouteTransitionSubstate(FileTransferCoordinatorState state)
        => state.PersistentState.FileTransferRouteTransition;

    internal static bool IsFileTransferRouteTransitionCurrent(
        FileTransferCoordinatorState state,
        FileTransferRouteTransitionAuthority authority,
        out string rejectionReason)
    {
        var operation = state.PersistentState.FileTransferRouteTransition.CurrentOperation;
        if (operation is null || operation.Value.Authority != authority)
        {
            rejectionReason = "filetransfer_route_transition_authority_not_current";
            return false;
        }

        return IsFileTransferRouteTransitionSourceScopeCurrent(
            state,
            authority,
            out rejectionReason);
    }

    internal static bool IsFileTransferRouteTransitionDraining(
        FileTransferCoordinatorState state,
        out FileTransferRouteTransitionAuthority authority)
    {
        if (state.PersistentState.FileTransferRouteTransition.CurrentOperation is
            {
                State: FileTransferRouteTransitionState.Draining,
            } operation &&
            IsFileTransferRouteTransitionSourceScopeCurrent(
                state,
                operation.Authority,
                out _))
        {
            authority = operation.Authority;
            return true;
        }

        authority = default;
        return false;
    }

    internal static bool TryGetPendingFileTransferRouteTransitionDisposition(
        FileTransferCoordinatorState state,
        out FileTransferRouteTransitionDispositionCommand command)
    {
        if (state.PersistentState.FileTransferRouteTransition.LastTerminalOperation is
            {
                DispositionCommand: { } pending,
                DispositionReceipt: null,
            })
        {
            command = pending;
            return true;
        }

        command = default;
        return false;
    }

    internal static string CreateFileTransferRouteTransitionPhysicalDrainFingerprint(
        IReadOnlyList<long> capturedPhysicalSendLeaseIds)
        => CreateRegularV4ToTunaPhysicalDrainFingerprint(capturedPhysicalSendLeaseIds);

    internal static FileTransferRouteTransitionDecision PrepareFileTransferRouteTransition(
        FileTransferCoordinatorState state,
        FileTransferRouteTransitionPrepareRequest request)
    {
        if (request.Kind == FileTransferRouteTransitionKind.RegularV4ToTunaV4)
        {
            if (!TryConvertRegularV4ToTunaPrepareRequest(
                    request,
                    out var compatibilityRequest,
                    out var conversionRejection))
            {
                return FileTransferRouteTransitionDecisionFor(
                    state,
                    FileTransferRouteTransitionDecisionStatus.Rejected,
                    conversionRejection);
            }

            return FromRegularV4ToTunaDecision(
                PrepareRegularV4ToTunaRouteTransition(state, compatibilityRequest));
        }

        var transitionState = state.PersistentState.FileTransferRouteTransition;
        if (transitionState.CurrentOperation is { } current)
        {
            if (FileTransferRouteTransitionPrepareRequestMatchesAuthority(
                    request,
                    current.Authority) &&
                IsFileTransferRouteTransitionSourceScopeCurrent(
                    state,
                    current.Authority,
                    out _))
            {
                return FileTransferRouteTransitionDecisionFor(
                    state,
                    FileTransferRouteTransitionDecisionStatus.Idempotent,
                    "filetransfer_route_transition_already_prepared",
                    current.Authority,
                    new FileTransferRouteTransitionPauseBindCommand(current.Authority),
                    current.DrainCommand);
            }

            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Rejected,
                "filetransfer_route_transition_conflict");
        }

        if (transitionState.LastTerminalOperation is
            {
                DispositionCommand: { } pendingDisposition,
                DispositionReceipt: null,
            } pendingTerminal)
        {
            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Rejected,
                "filetransfer_route_transition_disposition_receipt_pending",
                pendingTerminal.Authority,
                drainCommand: pendingTerminal.DrainCommand,
                dispositionCommand: pendingDisposition);
        }

        if (!TryValidateFileTransferRouteTransitionPrepareRequest(
                state,
                request,
                out var rejectionReason))
        {
            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Rejected,
                rejectionReason);
        }

        var owner = state.CurrentRuntimeOwner!.Value;
        var leg = state.CurrentLeg!;
        var transitionGeneration = Math.Max(
            1,
            transitionState.LastTransitionGeneration + 1);
        var authority = new FileTransferRouteTransitionAuthority(
            request.Kind,
            state.SessionId,
            state.TransferId,
            state.Direction,
            owner,
            request.SourceRoutePublication,
            request.SourceRouteCommit,
            request.SourceRoute,
            request.SourceProtocolVersion,
            request.SourceTransport,
            state.TerminalGeneration,
            state.CurrentLiveRouteEpoch?.EpochId ?? 0,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            request.DurableCheckpoint.DataSessionGeneration,
            request.ProofBoundary,
            request.DurableCheckpoint,
            request.TargetRoute,
            request.TargetProtocolVersion,
            request.TargetTransport,
            request.HandoffKind,
            transitionGeneration,
            IssueAuthorityId(
                "filetransfer_route_transition",
                state,
                checked((int)Math.Min(int.MaxValue, transitionGeneration))),
            request.PredecessorTransitionAuthorityId,
            NormalizeReason(request.Reason));
        var operation = new FileTransferRouteTransitionOperation(
            authority,
            FileTransferRouteTransitionState.Prepared);
        var nextState = state with
        {
            PersistentState = state.PersistentState with
            {
                FileTransferRouteTransition = transitionState with
                {
                    CurrentOperation = operation,
                    LastTransitionGeneration = transitionGeneration,
                },
            },
        };
        return FileTransferRouteTransitionDecisionFor(
            nextState,
            FileTransferRouteTransitionDecisionStatus.Prepared,
            "filetransfer_route_transition_prepared",
            authority,
            new FileTransferRouteTransitionPauseBindCommand(authority));
    }

    internal static FileTransferRouteTransitionDecision
        BeginFileTransferRouteTransitionPhysicalDrain(
            FileTransferCoordinatorState state,
            FileTransferRouteTransitionAuthority authority,
            FileTransferRouteTransitionPauseBindReceipt pauseBindReceipt,
            long capturedThroughPhysicalSendGeneration,
            IReadOnlyList<long> capturedPhysicalSendLeaseIds,
            string capturedPhysicalSendFingerprint)
    {
        if (authority.Kind == FileTransferRouteTransitionKind.RegularV4ToTunaV4)
        {
            var compatibilityAuthority =
                FileTransferRouteTransitionCompatibility.ToRegularV4ToTuna(authority);
            var compatibilityReceipt = new RegularV4ToTunaActivationPauseBindReceipt(
                new RegularV4ToTunaActivationPauseBindCommand(compatibilityAuthority),
                (RegularV4ToTunaActivationPauseBindStatus)(int)pauseBindReceipt.Status,
                pauseBindReceipt.Status switch
                {
                    FileTransferRouteTransitionPauseBindStatus.Installed
                        when string.Equals(
                            pauseBindReceipt.Reason,
                            "filetransfer_route_transition_pause_bound",
                            StringComparison.Ordinal) =>
                        "regular_v4_to_tuna_activation_pause_bound",
                    FileTransferRouteTransitionPauseBindStatus.Idempotent
                        when string.Equals(
                            pauseBindReceipt.Reason,
                            "filetransfer_route_transition_pause_bind_idempotent",
                            StringComparison.Ordinal) =>
                        "regular_v4_to_tuna_activation_pause_bind_idempotent",
                    _ => pauseBindReceipt.Reason,
                });
            return FromRegularV4ToTunaDecision(
                BeginRegularV4ToTunaPhysicalDrain(
                    state,
                    compatibilityAuthority,
                    compatibilityReceipt,
                    capturedThroughPhysicalSendGeneration,
                    capturedPhysicalSendLeaseIds,
                    capturedPhysicalSendFingerprint));
        }

        var transitionState = state.PersistentState.FileTransferRouteTransition;
        if (transitionState.CurrentOperation is not { } current ||
            current.Authority != authority)
        {
            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Rejected,
                "filetransfer_route_transition_authority_not_current");
        }

        if (current.State == FileTransferRouteTransitionState.Draining &&
            current.DrainCommand is { } existingCommand &&
            current.PauseBindReceipt is { } existingPauseReceipt &&
            IsExactOrCanonicalIdempotentFileTransferRouteTransitionPauseBindReceipt(
                existingPauseReceipt,
                pauseBindReceipt) &&
            existingCommand.CapturedThroughPhysicalSendGeneration ==
                capturedThroughPhysicalSendGeneration &&
            existingCommand.CapturedPhysicalSendCount ==
                capturedPhysicalSendLeaseIds.Count &&
            CapturedLeaseSetsEqual(
                existingCommand.CapturedPhysicalSendLeaseIds,
                capturedPhysicalSendLeaseIds) &&
            string.Equals(
                existingCommand.CapturedPhysicalSendFingerprint,
                capturedPhysicalSendFingerprint,
                StringComparison.Ordinal))
        {
            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Idempotent,
                "filetransfer_route_transition_drain_already_started",
                authority,
                pauseBindReceipt.Command,
                existingCommand);
        }

        var rejectionReason = "accepted";
        if (current.State != FileTransferRouteTransitionState.Prepared ||
            !IsFileTransferRouteTransitionSourceScopeCurrent(
                state,
                authority,
                out rejectionReason))
        {
            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Rejected,
                string.Equals(rejectionReason, "accepted", StringComparison.Ordinal)
                    ? "filetransfer_route_transition_not_prepared"
                    : rejectionReason);
        }

        var expectedPauseCommand = new FileTransferRouteTransitionPauseBindCommand(authority);
        if (pauseBindReceipt.Command != expectedPauseCommand ||
            pauseBindReceipt.Status is not (
                FileTransferRouteTransitionPauseBindStatus.Installed or
                FileTransferRouteTransitionPauseBindStatus.Idempotent) ||
            !IsCanonicalFileTransferRouteTransitionPauseBindReceipt(pauseBindReceipt))
        {
            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Rejected,
                "filetransfer_route_transition_pause_bind_receipt_mismatch");
        }

        if (!TryValidateCapturedPhysicalSendLeaseSet(
                capturedThroughPhysicalSendGeneration,
                capturedPhysicalSendLeaseIds,
                capturedPhysicalSendFingerprint,
                out rejectionReason))
        {
            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Rejected,
                rejectionReason.Replace(
                    "regular_v4_to_tuna_transition",
                    "filetransfer_route_transition",
                    StringComparison.Ordinal));
        }

        var capturedLeaseSnapshot = Array.AsReadOnly(
            capturedPhysicalSendLeaseIds.ToArray());
        var drainCommand = new FileTransferRouteTransitionPhysicalDrainCommand(
            authority,
            capturedThroughPhysicalSendGeneration,
            capturedLeaseSnapshot,
            capturedLeaseSnapshot.Count,
            capturedPhysicalSendFingerprint);
        var drainingOperation = current with
        {
            State = FileTransferRouteTransitionState.Draining,
            PauseBindReceipt = pauseBindReceipt,
            DrainCommand = drainCommand,
        };
        var nextState = state with
        {
            PersistentState = state.PersistentState with
            {
                FileTransferRouteTransition = transitionState with
                {
                    CurrentOperation = drainingOperation,
                },
            },
        };
        return FileTransferRouteTransitionDecisionFor(
            nextState,
            FileTransferRouteTransitionDecisionStatus.Draining,
            "filetransfer_route_transition_draining",
            authority,
            expectedPauseCommand,
            drainCommand);
    }

    internal static FileTransferRouteTransitionDecision
        StageFileTransferRouteTransitionSupersession(
            FileTransferCoordinatorState state,
            FileTransferRouteTransitionAuthority predecessorAuthority,
            FileTransferRouteTransitionPrepareRequest successorRequest)
    {
        var transitionState = state.PersistentState.FileTransferRouteTransition;
        if (transitionState.PendingSupersession is { } pending)
        {
            if (pending.PredecessorAuthority == predecessorAuthority &&
                FileTransferRouteTransitionPrepareRequestMatchesAuthority(
                    successorRequest,
                    pending.SuccessorAuthority))
            {
                return FileTransferRouteTransitionDecisionFor(
                    state,
                    FileTransferRouteTransitionDecisionStatus.Idempotent,
                    "filetransfer_route_transition_supersession_already_staged",
                    pending.SuccessorAuthority,
                    pending.PauseRebindCommand);
            }

            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Rejected,
                "filetransfer_route_transition_supersession_conflict");
        }

        var rejectionReason = "accepted";
        if (transitionState.CurrentOperation is not { } predecessor ||
            predecessor.Authority != predecessorAuthority ||
            predecessorAuthority.Kind !=
                FileTransferRouteTransitionKind.RegularV4ToTunaV4 ||
            successorRequest.Kind !=
                FileTransferRouteTransitionKind.UncommittedTunaToPostTunaFallbackV6 ||
            successorRequest.PredecessorTransitionAuthorityId !=
                predecessorAuthority.AuthorityId ||
            successorRequest.ProofBoundary is not
                FileTransferFallbackTransitionProofBoundary
                {
                    PredecessorTransitionAuthorityId: { } proofPredecessor,
                } ||
            proofPredecessor != predecessorAuthority.AuthorityId ||
            !IsFileTransferRouteTransitionSourceScopeCurrent(
                state,
                predecessorAuthority,
                out rejectionReason) ||
            !TryValidateFileTransferRouteTransitionPrepareRequest(
                state,
                successorRequest,
                out rejectionReason,
                allowCurrentOperation: true))
        {
            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Rejected,
                string.Equals(rejectionReason, "accepted", StringComparison.Ordinal)
                    ? "filetransfer_route_transition_supersession_invalid"
                    : rejectionReason);
        }

        var transitionGeneration = Math.Max(
            1,
            transitionState.LastTransitionGeneration + 1);
        var successor = CreateFileTransferRouteTransitionAuthority(
            state,
            successorRequest,
            transitionGeneration);
        var pauseRebind = new FileTransferRouteTransitionPauseBindCommand(successor);
        var staged = new FileTransferRouteTransitionSupersession(
            predecessorAuthority,
            successor,
            pauseRebind);
        var nextState = state with
        {
            PersistentState = state.PersistentState with
            {
                FileTransferRouteTransition = transitionState with
                {
                    PendingSupersession = staged,
                    LastTransitionGeneration = transitionGeneration,
                },
            },
        };
        return FileTransferRouteTransitionDecisionFor(
            nextState,
            FileTransferRouteTransitionDecisionStatus.SupersessionStaged,
            "filetransfer_route_transition_supersession_staged",
            successor,
            pauseRebind);
    }

    internal static FileTransferRouteTransitionDecision
        CommitFileTransferRouteTransitionSupersession(
            FileTransferCoordinatorState state,
            FileTransferRouteTransitionPauseBindReceipt pauseRebindReceipt)
    {
        var transitionState = state.PersistentState.FileTransferRouteTransition;
        if (transitionState.PendingSupersession is not { } pending ||
            transitionState.CurrentOperation is not { } predecessor ||
            predecessor.Authority != pending.PredecessorAuthority)
        {
            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Rejected,
                "filetransfer_route_transition_supersession_not_pending");
        }

        var expectedCommand = pending.PauseRebindCommand;
        if (pauseRebindReceipt.Command != expectedCommand ||
            pauseRebindReceipt.Status is not (
                FileTransferRouteTransitionPauseBindStatus.Installed or
                FileTransferRouteTransitionPauseBindStatus.Idempotent) ||
            !IsCanonicalFileTransferRouteTransitionPauseBindReceipt(pauseRebindReceipt))
        {
            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Rejected,
                "filetransfer_route_transition_supersession_pause_receipt_mismatch");
        }

        var supersededDisposition = new FileTransferRouteTransitionDispositionCommand(
            predecessor.Authority,
            FileTransferRouteTransitionDispositionKind.Superseded,
            RouteCommitToken: null,
            RoutePublicationToken: null,
            "filetransfer_route_transition_superseded");
        var superseded = predecessor with
        {
            State = FileTransferRouteTransitionState.Invalidated,
            DispositionCommand = supersededDisposition,
        };
        var successorDrain = predecessor.DrainCommand is { } drain
            ? drain with { Authority = pending.SuccessorAuthority }
            : (FileTransferRouteTransitionPhysicalDrainCommand?)null;
        var successor = new FileTransferRouteTransitionOperation(
            pending.SuccessorAuthority,
            predecessor.State,
            pauseRebindReceipt,
            successorDrain);
        var nextState = state with
        {
            PersistentState = state.PersistentState with
            {
                FileTransferRouteTransition = transitionState with
                {
                    CurrentOperation = successor,
                    PendingSupersession = null,
                    LastTerminalOperation = superseded,
                },
            },
        };
        return FileTransferRouteTransitionDecisionFor(
            nextState,
            FileTransferRouteTransitionDecisionStatus.Prepared,
            "filetransfer_route_transition_supersession_committed",
            successor.Authority,
            expectedCommand,
            successorDrain,
            supersededDisposition);
    }

    internal static FileTransferRouteTransitionDecision
        InvalidateFileTransferRouteTransition(
            FileTransferCoordinatorState state,
            FileTransferRouteTransitionAuthority authority,
            bool terminalInvalidation,
            string reason)
    {
        if (authority.Kind == FileTransferRouteTransitionKind.RegularV4ToTunaV4)
        {
            return FromRegularV4ToTunaDecision(
                InvalidateRegularV4ToTunaRouteTransition(
                    state,
                    FileTransferRouteTransitionCompatibility.ToRegularV4ToTuna(authority),
                    terminalInvalidation,
                    reason));
        }

        var transitionState = state.PersistentState.FileTransferRouteTransition;
        var normalizedReason = NormalizeReason(reason);
        var kind = terminalInvalidation
            ? FileTransferRouteTransitionDispositionKind.TerminalInvalidated
            : FileTransferRouteTransitionDispositionKind.RolledBack;
        if (transitionState.LastTerminalOperation is
            {
                State: FileTransferRouteTransitionState.Invalidated,
                DispositionCommand: { } terminalDisposition,
            } terminal &&
            terminal.Authority == authority)
        {
            if (terminalDisposition.Kind != kind ||
                !string.Equals(
                    terminalDisposition.Reason,
                    normalizedReason,
                    StringComparison.Ordinal))
            {
                return FileTransferRouteTransitionDecisionFor(
                    state,
                    FileTransferRouteTransitionDecisionStatus.Rejected,
                    "filetransfer_route_transition_invalidation_duplicate_mismatch");
            }

            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Idempotent,
                "filetransfer_route_transition_already_invalidated",
                authority,
                drainCommand: terminal.DrainCommand,
                dispositionCommand: terminalDisposition);
        }

        if (transitionState.CurrentOperation is not { } current ||
            current.Authority != authority)
        {
            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Rejected,
                "filetransfer_route_transition_authority_not_current");
        }

        if (!terminalInvalidation &&
            !IsFileTransferRouteTransitionSourceScopeCurrent(
                state,
                authority,
                out var rejectionReason))
        {
            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Rejected,
                rejectionReason);
        }

        var disposition = new FileTransferRouteTransitionDispositionCommand(
            authority,
            kind,
            RouteCommitToken: null,
            RoutePublicationToken: null,
            normalizedReason);
        var invalidated = current with
        {
            State = FileTransferRouteTransitionState.Invalidated,
            DispositionCommand = disposition,
        };
        var nextState = state with
        {
            PersistentState = state.PersistentState with
            {
                FileTransferRouteTransition = transitionState with
                {
                    CurrentOperation = null,
                    PendingSupersession = null,
                    LastTerminalOperation = invalidated,
                },
            },
        };
        return FileTransferRouteTransitionDecisionFor(
            nextState,
            FileTransferRouteTransitionDecisionStatus.Invalidated,
            normalizedReason,
            authority,
            drainCommand: invalidated.DrainCommand,
            dispositionCommand: disposition);
    }

    internal static FileTransferRouteTransitionDecision
        ApplyFileTransferRouteTransitionDispositionReceipt(
            FileTransferCoordinatorState state,
            FileTransferRouteTransitionDispositionReceipt receipt)
    {
        if (receipt.Command.Authority.Kind ==
            FileTransferRouteTransitionKind.RegularV4ToTunaV4)
        {
            var authority =
                FileTransferRouteTransitionCompatibility.ToRegularV4ToTuna(
                    receipt.Command.Authority);
            var command = new RegularV4ToTunaRouteTransitionDispositionCommand(
                authority,
                (RegularV4ToTunaRouteTransitionDispositionKind)(int)receipt.Command.Kind,
                receipt.Command.RouteCommitToken,
                receipt.Command.RoutePublicationToken,
                receipt.Command.Reason)
            {
                FinalProof = receipt.Command.FinalProof,
            };
            return FromRegularV4ToTunaDecision(
                ApplyRegularV4ToTunaRouteTransitionDispositionReceipt(
                    state,
                    new RegularV4ToTunaRouteTransitionDispositionReceipt(
                        command,
                        (RegularV4ToTunaRouteTransitionDispositionApplyStatus)(int)receipt.Status,
                        receipt.Reason)));
        }

        var transitionState = state.PersistentState.FileTransferRouteTransition;
        if (transitionState.LastTerminalOperation is not
            {
                DispositionCommand: { } expectedCommand,
            } terminal ||
            receipt.Command != expectedCommand)
        {
            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Rejected,
                "filetransfer_route_transition_disposition_receipt_not_current");
        }

        if (terminal.DispositionReceipt is { } appliedReceipt)
        {
            if (receipt == appliedReceipt ||
                receipt.Command == appliedReceipt.Command &&
                receipt.Status ==
                    FileTransferRouteTransitionDispositionApplyStatus.Idempotent &&
                string.Equals(
                    receipt.Reason,
                    "filetransfer_route_transition_disposition_idempotent",
                    StringComparison.Ordinal))
            {
                return FileTransferRouteTransitionDecisionFor(
                    state,
                    FileTransferRouteTransitionDecisionStatus.Idempotent,
                    "filetransfer_route_transition_disposition_receipt_already_applied",
                    terminal.Authority,
                    drainCommand: terminal.DrainCommand,
                    dispositionCommand: expectedCommand);
            }

            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Rejected,
                "filetransfer_route_transition_disposition_receipt_duplicate_mismatch");
        }

        if (receipt.Status is not (
                FileTransferRouteTransitionDispositionApplyStatus.Applied or
                FileTransferRouteTransitionDispositionApplyStatus.Idempotent) ||
            !string.Equals(
                receipt.Reason,
                receipt.Status == FileTransferRouteTransitionDispositionApplyStatus.Idempotent
                    ? "filetransfer_route_transition_disposition_idempotent"
                    : receipt.Command.Kind switch
                    {
                        FileTransferRouteTransitionDispositionKind.Committed =>
                            "filetransfer_route_transition_committed",
                        FileTransferRouteTransitionDispositionKind.RolledBack =>
                            "filetransfer_route_transition_rolled_back",
                        FileTransferRouteTransitionDispositionKind.Superseded =>
                            "filetransfer_route_transition_superseded",
                        _ => "filetransfer_route_transition_terminal_invalidated",
                    },
                StringComparison.Ordinal))
        {
            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Rejected,
                "filetransfer_route_transition_disposition_receipt_rejected");
        }

        var terminalWithReceipt = terminal with { DispositionReceipt = receipt };
        var nextState = state with
        {
            PersistentState = state.PersistentState with
            {
                FileTransferRouteTransition = transitionState with
                {
                    LastTerminalOperation = terminalWithReceipt,
                },
            },
        };
        return FileTransferRouteTransitionDecisionFor(
            nextState,
            terminal.State == FileTransferRouteTransitionState.Committed
                ? FileTransferRouteTransitionDecisionStatus.Committed
                : FileTransferRouteTransitionDecisionStatus.Invalidated,
            "filetransfer_route_transition_disposition_receipt_applied",
            terminal.Authority,
            drainCommand: terminal.DrainCommand,
            dispositionCommand: expectedCommand);
    }

    internal static RegularV4ToTunaRouteTransitionSubstate
        GetRegularV4ToTunaRouteTransitionSubstate(FileTransferCoordinatorState state)
        => state.PersistentState.RegularV4ToTunaRouteTransition;

    internal static bool IsRegularV4ToTunaRouteTransitionDispositionCommandValid(
        RegularV4ToTunaRouteTransitionDispositionCommand command)
    {
        var authority = command.Authority;
        if (authority.IsDefault ||
            string.IsNullOrWhiteSpace(command.Reason) ||
            !string.Equals(
                command.Reason,
                NormalizeReason(command.Reason),
                StringComparison.Ordinal))
        {
            return false;
        }

        if (command.Kind is
            RegularV4ToTunaRouteTransitionDispositionKind.RolledBack or
            RegularV4ToTunaRouteTransitionDispositionKind.TerminalInvalidated)
        {
            return command.RouteCommitToken is null &&
                   command.RoutePublicationToken is null &&
                   command.FinalProof is null;
        }

        if (command.Kind == RegularV4ToTunaRouteTransitionDispositionKind.Superseded)
        {
            return command.RouteCommitToken is null &&
                   command.FinalProof is null &&
                   command.RoutePublicationToken is { IsDefault: false } fallback &&
                   string.Equals(
                       fallback.SessionId,
                       authority.SessionId,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       fallback.TransferId,
                       authority.TransferId,
                       StringComparison.Ordinal) &&
                   fallback.Direction == authority.Direction &&
                   fallback.RuntimeOwner.SessionId == fallback.SessionId &&
                   fallback.RuntimeOwner.TransferId == fallback.TransferId &&
                   fallback.RuntimeOwner.Direction == fallback.Direction &&
                   fallback.RuntimeOwner.Route == fallback.Route &&
                   string.Equals(
                       fallback.RuntimeOwner.RouteToken,
                       fallback.RouteToken,
                       StringComparison.Ordinal) &&
                   fallback.RuntimeOwner.ProtocolVersion == fallback.ProtocolVersion &&
                   fallback.RuntimeOwner.LiveRouteEpoch == fallback.LiveRouteEpoch &&
                   fallback.RuntimeOwner.RuntimeGeneration == fallback.RuntimeGeneration &&
                   fallback.RuntimeOwner.TerminalGeneration == fallback.TerminalGeneration &&
                   fallback.TerminalGeneration == authority.SourceTerminalGeneration &&
                   fallback.Route == FileTransferRoute.PostTunaFallbackV6 &&
                   fallback.ProtocolVersion == FileTransferProtocol.ProtocolVersionV6 &&
                   fallback.TargetRoute == FileTransferRoute.PostTunaFallbackV6 &&
                   fallback.TargetProtocolVersion ==
                       FileTransferProtocol.ProtocolVersionV6 &&
                   fallback.TargetTransport == FileTransferTransportKind.RegularNkn &&
                   fallback.ActionKind ==
                       FileTransferCoordinatorActionKind.RoutePublication &&
                   fallback.RouteCommitGeneration is null;
        }

        if (command.Kind != RegularV4ToTunaRouteTransitionDispositionKind.Committed ||
            command.RouteCommitToken is not { IsDefault: false } commit ||
            command.RoutePublicationToken is not { IsDefault: false } publication ||
            command.FinalProof is not { } finalProof ||
            !RegularV4ToTunaProofMatchesTarget(finalProof) ||
            finalProof.PathProbeState != RuntimeUnlockPathProbeState.Acked)
        {
            return false;
        }

        var expectedProbeId =
            finalProof.SamePathTunaProbeAckId ??
            finalProof.PathProbeId ??
            string.Empty;
        return finalProof.AdmissionToken == authority.RuntimeUnlockAdmission &&
               finalProof.TransactionGeneration ==
                   authority.AcceptedLaunchBoundary.TransactionGeneration &&
               finalProof.OfferGeneration ==
                   authority.AcceptedLaunchBoundary.OfferGeneration &&
               finalProof.TunaPathLeaseGeneration ==
                   authority.AcceptedLaunchBoundary.TunaPathLeaseGeneration &&
               string.Equals(
                   finalProof.TunaPathLeaseListenerRunId,
                   authority.AcceptedLaunchBoundary.TunaPathLeaseListenerRunId,
                   StringComparison.Ordinal) &&
               string.Equals(commit.SessionId, authority.SessionId, StringComparison.Ordinal) &&
               string.Equals(commit.TransferId, authority.TransferId, StringComparison.Ordinal) &&
               commit.Direction == authority.Direction &&
               commit.RuntimeOwner.SessionId == commit.SessionId &&
               commit.RuntimeOwner.TransferId == commit.TransferId &&
               commit.RuntimeOwner.Direction == commit.Direction &&
               commit.RuntimeOwner.Route == commit.Route &&
               string.Equals(
                   commit.RuntimeOwner.RouteToken,
                   commit.RouteToken,
                   StringComparison.Ordinal) &&
               commit.RuntimeOwner.ProtocolVersion == commit.ProtocolVersion &&
               commit.RuntimeOwner.LiveRouteEpoch == commit.LiveRouteEpoch &&
               commit.RuntimeOwner.RuntimeGeneration == commit.RuntimeGeneration &&
               commit.RuntimeOwner.TerminalGeneration == commit.TerminalGeneration &&
               commit.RuntimeUnlockAdmission == authority.RuntimeUnlockAdmission &&
               commit.TransactionGeneration == finalProof.TransactionGeneration &&
               commit.OfferGeneration == finalProof.OfferGeneration &&
               commit.TunaPathLeaseGeneration == finalProof.TunaPathLeaseGeneration &&
               string.Equals(
                   commit.TunaPathLeaseListenerRunId,
                   finalProof.TunaPathLeaseListenerRunId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   commit.SamePathTunaProbeAckId,
                   expectedProbeId,
                   StringComparison.Ordinal) &&
               commit.TerminalGeneration == authority.SourceTerminalGeneration &&
               commit.Route == authority.TargetRoute &&
               commit.ProtocolVersion == authority.TargetProtocolVersion &&
               commit.TargetRoute == authority.TargetRoute &&
               commit.TargetProtocolVersion == authority.TargetProtocolVersion &&
               commit.TargetTransport == authority.TargetTransport &&
               publication.RuntimeOwner == commit.RuntimeOwner &&
               string.Equals(
                   publication.SessionId,
                   commit.SessionId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   publication.TransferId,
                   commit.TransferId,
                   StringComparison.Ordinal) &&
               publication.Direction == commit.Direction &&
               publication.Route == commit.Route &&
               publication.ProtocolVersion == commit.ProtocolVersion &&
               publication.LiveRouteEpoch == commit.LiveRouteEpoch &&
               publication.RuntimeGeneration == commit.RuntimeGeneration &&
               publication.TerminalGeneration == commit.TerminalGeneration &&
               publication.LegGeneration == commit.LegGeneration &&
               publication.RouteCommitGeneration == commit.CommitGeneration &&
               publication.TargetRoute == authority.TargetRoute &&
               publication.TargetProtocolVersion == authority.TargetProtocolVersion &&
               publication.TargetTransport == authority.TargetTransport &&
               publication.ActionKind ==
                   FileTransferCoordinatorActionKind.RouteCommitPublication;
    }

    internal static bool IsRegularV4ToTunaRouteTransitionCurrent(
        FileTransferCoordinatorState state,
        RegularV4ToTunaRouteTransitionAuthority authority,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        var operation = state.PersistentState.RegularV4ToTunaRouteTransition.CurrentOperation;
        if (operation is null || operation.Value.Authority != authority)
        {
            rejectionReason = "regular_v4_to_tuna_transition_authority_not_current";
            return false;
        }

        return IsRegularV4ToTunaRouteTransitionSourceScopeCurrent(
            state,
            authority,
            out rejectionReason);
    }

    internal static bool IsRegularV4ToTunaRouteTransitionDraining(
        FileTransferCoordinatorState state,
        out RegularV4ToTunaRouteTransitionAuthority authority)
    {
        if (state.PersistentState.RegularV4ToTunaRouteTransition.CurrentOperation is
            {
                State: RegularV4ToTunaRouteTransitionState.Draining,
            } operation &&
            IsRegularV4ToTunaRouteTransitionSourceScopeCurrent(
                state,
                operation.Authority,
                out _))
        {
            authority = operation.Authority;
            return true;
        }

        authority = default;
        return false;
    }

    internal static bool TryGetPendingRegularV4ToTunaRouteTransitionDisposition(
        FileTransferCoordinatorState state,
        out RegularV4ToTunaRouteTransitionDispositionCommand command)
    {
        if (state.PersistentState.RegularV4ToTunaRouteTransition.LastTerminalOperation is
            {
                DispositionCommand: { } pending,
                DispositionReceipt: null,
            })
        {
            command = pending;
            return true;
        }

        command = default;
        return false;
    }

    internal static string CreateRegularV4ToTunaPhysicalDrainFingerprint(
        IReadOnlyList<long> capturedPhysicalSendLeaseIds)
    {
        ArgumentNullException.ThrowIfNull(capturedPhysicalSendLeaseIds);
        var canonical = string.Join(
            ",",
            capturedPhysicalSendLeaseIds.Select(id =>
                id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static RegularV4ToTunaRouteTransitionDecision
        PrepareRegularV4ToTunaRouteTransition(
            FileTransferCoordinatorState state,
            RegularV4ToTunaRouteTransitionPrepareRequest request)
    {
        var transitionState = state.PersistentState.RegularV4ToTunaRouteTransition;
        if (transitionState.CurrentOperation is { } current)
        {
            if (RegularV4ToTunaPrepareRequestMatchesAuthority(request, current.Authority) &&
                IsRegularV4ToTunaRouteTransitionSourceScopeCurrent(
                    state,
                    current.Authority,
                    out _))
            {
                return RegularV4ToTunaDecision(
                    state,
                    RegularV4ToTunaRouteTransitionDecisionStatus.Idempotent,
                    "regular_v4_to_tuna_transition_already_prepared",
                    current.Authority,
                    new RegularV4ToTunaActivationPauseBindCommand(current.Authority),
                    current.DrainCommand);
            }

            return RegularV4ToTunaDecision(
                state,
                RegularV4ToTunaRouteTransitionDecisionStatus.Rejected,
                "regular_v4_to_tuna_transition_conflict");
        }

        if (transitionState.LastTerminalOperation is
            {
                State: RegularV4ToTunaRouteTransitionState.Committed,
            } terminal &&
            RegularV4ToTunaPrepareRequestMatchesAuthority(request, terminal.Authority))
        {
            return RegularV4ToTunaDecision(
                state,
                RegularV4ToTunaRouteTransitionDecisionStatus.Idempotent,
                "regular_v4_to_tuna_transition_already_committed",
                terminal.Authority,
                dispositionCommand: terminal.DispositionCommand,
                routeDecision: null);
        }

        if (transitionState.LastTerminalOperation is
            {
                State: RegularV4ToTunaRouteTransitionState.Invalidated,
                DispositionCommand:
                {
                    Kind:
                        RegularV4ToTunaRouteTransitionDispositionKind.TerminalInvalidated,
                },
            } terminalInvalidated &&
            RegularV4ToTunaPrepareRequestMatchesAuthority(
                request,
                terminalInvalidated.Authority))
        {
            return RegularV4ToTunaDecision(
                state,
                RegularV4ToTunaRouteTransitionDecisionStatus.Idempotent,
                "regular_v4_to_tuna_transition_terminally_invalidated",
                terminalInvalidated.Authority,
                dispositionCommand: terminalInvalidated.DispositionCommand);
        }

        if (transitionState.LastTerminalOperation is
            {
                DispositionCommand: { } pendingDisposition,
                DispositionReceipt: null,
            } pendingTerminal)
        {
            return RegularV4ToTunaDecision(
                state,
                RegularV4ToTunaRouteTransitionDecisionStatus.Rejected,
                "regular_v4_to_tuna_transition_disposition_receipt_pending",
                pendingTerminal.Authority,
                drainCommand: pendingTerminal.DrainCommand,
                dispositionCommand: pendingDisposition);
        }

        if (!TryValidateRegularV4ToTunaPrepareRequest(
                state,
                request,
                out var rejectionReason))
        {
            return RegularV4ToTunaDecision(
                state,
                RegularV4ToTunaRouteTransitionDecisionStatus.Rejected,
                rejectionReason);
        }

        var owner = state.CurrentRuntimeOwner!.Value;
        var leg = state.CurrentLeg!;
        var transitionGeneration = Math.Max(
            1,
            transitionState.LastTransitionGeneration + 1);
        var authority = new RegularV4ToTunaRouteTransitionAuthority(
            state.SessionId,
            state.TransferId,
            state.Direction,
            owner,
            request.SourceRoutePublication,
            state.TerminalGeneration,
            state.CurrentLiveRouteEpoch?.EpochId ?? 0,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            request.DurableCheckpoint.DataSessionGeneration,
            request.RuntimeUnlockAdmission,
            request.PendingPreCommitProbe,
            request.AcceptedLaunchBoundary,
            request.LaunchGeneration,
            request.DurableCheckpoint,
            FileTransferRoute.FileTunaV4,
            FileTransferProtocol.ProtocolVersionV4,
            FileTransferTransportKind.Tuna,
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            transitionGeneration,
            IssueAuthorityId(
                "regular_v4_to_tuna_transition",
                state,
                checked((int)Math.Min(int.MaxValue, transitionGeneration))),
            NormalizeReason(request.Reason));
        var operation = new RegularV4ToTunaRouteTransitionOperation(
            authority,
            RegularV4ToTunaRouteTransitionState.Prepared);
        var nextState = state with
        {
            PersistentState = state.PersistentState with
            {
                RegularV4ToTunaRouteTransition = transitionState with
                {
                    CurrentOperation = operation,
                    LastTransitionGeneration = transitionGeneration,
                },
            },
        };
        return RegularV4ToTunaDecision(
            nextState,
            RegularV4ToTunaRouteTransitionDecisionStatus.Prepared,
            "regular_v4_to_tuna_transition_prepared",
            authority,
            new RegularV4ToTunaActivationPauseBindCommand(authority));
    }

    internal static RegularV4ToTunaRouteTransitionDecision
        BeginRegularV4ToTunaPhysicalDrain(
            FileTransferCoordinatorState state,
            RegularV4ToTunaRouteTransitionAuthority authority,
            RegularV4ToTunaActivationPauseBindReceipt pauseBindReceipt,
            long capturedThroughPhysicalSendGeneration,
            IReadOnlyList<long> capturedPhysicalSendLeaseIds,
            string capturedPhysicalSendFingerprint)
    {
        var transitionState = state.PersistentState.RegularV4ToTunaRouteTransition;
        if (transitionState.CurrentOperation is not { } current ||
            current.Authority != authority)
        {
            return RegularV4ToTunaDecision(
                state,
                RegularV4ToTunaRouteTransitionDecisionStatus.Rejected,
                "regular_v4_to_tuna_transition_authority_not_current");
        }

        if (current.State == RegularV4ToTunaRouteTransitionState.Draining &&
            current.DrainCommand is { } existingCommand &&
            current.PauseBindReceipt is { } existingPauseReceipt &&
            IsExactOrCanonicalIdempotentRegularV4ToTunaPauseBindReceipt(
                existingPauseReceipt,
                pauseBindReceipt) &&
            existingCommand.CapturedThroughPhysicalSendGeneration ==
                capturedThroughPhysicalSendGeneration &&
            existingCommand.CapturedPhysicalSendCount ==
                capturedPhysicalSendLeaseIds.Count &&
            CapturedLeaseSetsEqual(
                existingCommand.CapturedPhysicalSendLeaseIds,
                capturedPhysicalSendLeaseIds) &&
            string.Equals(
                existingCommand.CapturedPhysicalSendFingerprint,
                capturedPhysicalSendFingerprint,
                StringComparison.Ordinal))
        {
            return RegularV4ToTunaDecision(
                state,
                RegularV4ToTunaRouteTransitionDecisionStatus.Idempotent,
                "regular_v4_to_tuna_transition_drain_already_started",
                authority,
                pauseBindReceipt.Command,
                existingCommand);
        }

        if (current.State != RegularV4ToTunaRouteTransitionState.Prepared)
        {
            return RegularV4ToTunaDecision(
                state,
                RegularV4ToTunaRouteTransitionDecisionStatus.Rejected,
                "regular_v4_to_tuna_transition_not_prepared");
        }

        if (!IsRegularV4ToTunaRouteTransitionSourceScopeCurrent(
                state,
                authority,
                out var rejectionReason))
        {
            return RegularV4ToTunaDecision(
                state,
                RegularV4ToTunaRouteTransitionDecisionStatus.Rejected,
                rejectionReason);
        }

        var expectedPauseCommand = new RegularV4ToTunaActivationPauseBindCommand(authority);
        if (pauseBindReceipt.Command != expectedPauseCommand ||
            pauseBindReceipt.Status is not (
                RegularV4ToTunaActivationPauseBindStatus.Installed or
                RegularV4ToTunaActivationPauseBindStatus.Idempotent) ||
            !IsCanonicalRegularV4ToTunaPauseBindReceipt(pauseBindReceipt))
        {
            return RegularV4ToTunaDecision(
                state,
                RegularV4ToTunaRouteTransitionDecisionStatus.Rejected,
                "regular_v4_to_tuna_transition_pause_bind_receipt_mismatch");
        }

        if (!TryValidateCapturedPhysicalSendLeaseSet(
                capturedThroughPhysicalSendGeneration,
                capturedPhysicalSendLeaseIds,
                capturedPhysicalSendFingerprint,
                out rejectionReason))
        {
            return RegularV4ToTunaDecision(
                state,
                RegularV4ToTunaRouteTransitionDecisionStatus.Rejected,
                rejectionReason);
        }

        var capturedLeaseSnapshot = Array.AsReadOnly(
            capturedPhysicalSendLeaseIds.ToArray());
        var drainCommand = new RegularV4ToTunaPhysicalDrainCommand(
            authority,
            capturedThroughPhysicalSendGeneration,
            capturedLeaseSnapshot,
            capturedLeaseSnapshot.Count,
            capturedPhysicalSendFingerprint);
        var drainingOperation = current with
        {
            State = RegularV4ToTunaRouteTransitionState.Draining,
            PauseBindReceipt = pauseBindReceipt,
            DrainCommand = drainCommand,
        };
        var nextState = state with
        {
            PersistentState = state.PersistentState with
            {
                RegularV4ToTunaRouteTransition = transitionState with
                {
                    CurrentOperation = drainingOperation,
                },
            },
        };
        return RegularV4ToTunaDecision(
            nextState,
            RegularV4ToTunaRouteTransitionDecisionStatus.Draining,
            "regular_v4_to_tuna_transition_draining",
            authority,
            expectedPauseCommand,
            drainCommand);
    }

    internal static RegularV4FrontierRepairPressureBindingDecision
        PrepareRegularV4ToTunaRouteTransitionPressureSettlement(
            FileTransferCoordinatorState state,
            RegularV4ToTunaRouteTransitionAuthority authority,
            RegularV4ToTunaPhysicalDrainReceipt drainReceipt,
            RuntimeUnlockRouteCommitProof finalProof)
    {
        if (!TryValidateRegularV4ToTunaDrainReceipt(
                state,
                authority,
                drainReceipt,
                out var rejectionReason) ||
            !TryValidateRegularV4ToTunaFinalProof(
                state,
                authority,
                finalProof,
                out rejectionReason))
        {
            return RegularV4PressureBindingDecision(
                state,
                RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
                rejectionReason);
        }

        var pressureState = state.PersistentState.RegularV4FrontierRepairPressure;
        if (pressureState.PendingBinding is not null ||
            pressureState.RetainedPredecessorBinding is not null)
        {
            return RegularV4PressureBindingDecision(
                state,
                RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
                "regular_v4_to_tuna_transition_pressure_lineage_pending");
        }

        if (pressureState.InstalledBinding is not { } installed)
        {
            return RegularV4PressureBindingDecision(
                state,
                RegularV4FrontierRepairPressureBindingDecisionStatus.Idempotent,
                "regular_v4_to_tuna_transition_pressure_not_installed");
        }

        return PrepareRegularV4FrontierRepairPressureSettlement(
            state,
            CreateRegularV4ToTunaTransitionPressureSettlement(installed.Authority));
    }

    internal static RegularV4ToTunaRouteTransitionDecision
        CommitRegularV4ToTunaRouteTransition(
            FileTransferCoordinatorState state,
            RegularV4ToTunaRouteTransitionAuthority authority,
            RegularV4ToTunaPhysicalDrainReceipt drainReceipt,
            RuntimeUnlockRouteCommitProof finalProof,
            FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt?
                pressureSettlementReceipt = null)
    {
        var transitionState = state.PersistentState.RegularV4ToTunaRouteTransition;
        if (transitionState.LastTerminalOperation is
            {
                State: RegularV4ToTunaRouteTransitionState.Committed,
            } terminal &&
            terminal.Authority == authority &&
            terminal.DrainReceipt is { } committedDrainReceipt &&
            IsExactOrCanonicalIdempotentRegularV4ToTunaDrainReceipt(
                committedDrainReceipt,
                drainReceipt) &&
            terminal.FinalProof == finalProof &&
            IsExactRegularV4ToTunaPressureReceiptDuplicate(
                terminal.PressureSettlementReceipt,
                pressureSettlementReceipt))
        {
            return RegularV4ToTunaDecision(
                state,
                RegularV4ToTunaRouteTransitionDecisionStatus.Idempotent,
                "regular_v4_to_tuna_transition_already_committed",
                authority,
                drainCommand: terminal.DrainCommand,
                dispositionCommand: terminal.DispositionCommand);
        }

        if (!TryValidateRegularV4ToTunaDrainReceipt(
                state,
                authority,
                drainReceipt,
                out var rejectionReason) ||
            !TryValidateRegularV4ToTunaFinalProof(
                state,
                authority,
                finalProof,
                out rejectionReason))
        {
            return RegularV4ToTunaDecision(
                state,
                RegularV4ToTunaRouteTransitionDecisionStatus.Rejected,
                rejectionReason);
        }

        var projectedState = state;
        var pressureState = state.PersistentState.RegularV4FrontierRepairPressure;
        if (pressureState.PendingBinding is not null ||
            pressureState.RetainedPredecessorBinding is not null)
        {
            return RegularV4ToTunaDecision(
                state,
                RegularV4ToTunaRouteTransitionDecisionStatus.Rejected,
                "regular_v4_to_tuna_transition_pressure_lineage_pending");
        }

        if (pressureState.InstalledBinding is { } installed)
        {
            var expectedSettlement =
                CreateRegularV4ToTunaTransitionPressureSettlement(installed.Authority);
            var preparedPressure =
                PrepareRegularV4FrontierRepairPressureSettlement(state, expectedSettlement);
            if (pressureSettlementReceipt is null ||
                preparedPressure.Status !=
                    RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted ||
                preparedPressure.SettlementCommand is not { } expectedCommand ||
                pressureSettlementReceipt.Command != expectedCommand ||
                !IsCanonicalRegularV4ToTunaPressureSettlementReceipt(
                    pressureSettlementReceipt))
            {
                return RegularV4ToTunaDecision(
                    state,
                    RegularV4ToTunaRouteTransitionDecisionStatus.Rejected,
                    "regular_v4_to_tuna_transition_pressure_apply_receipt_missing");
            }

            var pressureCommit = CommitRegularV4FrontierRepairPressureSettlement(
                state,
                pressureSettlementReceipt);
            if (pressureCommit.Status !=
                RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted)
            {
                return RegularV4ToTunaDecision(
                    state,
                    RegularV4ToTunaRouteTransitionDecisionStatus.Rejected,
                    pressureCommit.Reason);
            }

            projectedState =
                pressureCommit.State.CurrentRepairReplayDecision is { } currentRepair &&
                currentRepair.AuthorityId == installed.Authority.AuthorityId
                    ? pressureCommit.State with { CurrentRepairReplayDecision = null }
                    : pressureCommit.State;
        }
        else if (pressureSettlementReceipt is not null)
        {
            return RegularV4ToTunaDecision(
                state,
                RegularV4ToTunaRouteTransitionDecisionStatus.Rejected,
                "regular_v4_to_tuna_transition_pressure_apply_receipt_unexpected");
        }

        var targetSelection = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var coordinatorEvent = new FileTransferCoordinatorEvent(
            FileTransferCoordinatorEventKind.TunaActivated,
            targetSelection,
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna,
            NormalizeReason(authority.Reason),
            FileTransferLegState.Active,
            CanSendData: true,
            authority.DurableCheckpoint.ContiguousCommittedChunkIndex,
            authority.DurableCheckpoint.DurableHighestChunkIndex,
            TransportEpochId: 0,
            BridgeRecoveryGeneration: 0);
        projectedState = CloneCoordinatorStateGraph(projectedState);
        projectedState = projectedState with
        {
            PersistentState = projectedState.PersistentState with
            {
                RegularV4ToTunaRouteTransition = transitionState with
                {
                    CurrentOperation = null,
                },
            },
        };
        var routeDecision = TransitionToRoute(
            coordinatorEvent,
            projectedState,
            NormalizeReason(authority.Reason),
            recoverEpoch: true);
        if (routeDecision.MutationRejectedReason is { } routeRejectionReason)
        {
            return RegularV4ToTunaDecision(
                state,
                RegularV4ToTunaRouteTransitionDecisionStatus.Rejected,
                routeRejectionReason);
        }

        if (routeDecision.State.CurrentRuntimeOwner is not { } runtimeOwner ||
            routeDecision.State.CurrentLeg is not { } targetLeg)
        {
            return RegularV4ToTunaDecision(
                state,
                RegularV4ToTunaRouteTransitionDecisionStatus.Rejected,
                "regular_v4_to_tuna_transition_target_scope_missing");
        }

        var commitGeneration = Math.Max(1, state.LastRouteCommitGeneration + 1);
        var routeCommitToken = new RouteCommitToken(
            routeDecision.State.SessionId,
            routeDecision.State.TransferId,
            routeDecision.State.Direction,
            runtimeOwner,
            authority.RuntimeUnlockAdmission,
            routeDecision.State.RouteSelection.Route,
            routeDecision.State.RouteSelection.TelemetryToken,
            routeDecision.State.RouteSelection.ProtocolVersion,
            routeDecision.State.CurrentLiveRouteEpoch?.EpochId ?? 0,
            runtimeOwner.RuntimeGeneration,
            routeDecision.State.TerminalGeneration,
            targetLeg.Generation,
            finalProof.TransactionGeneration,
            finalProof.OfferGeneration,
            finalProof.TunaPathLeaseGeneration,
            finalProof.TunaPathLeaseListenerRunId,
            finalProof.SamePathTunaProbeAckId ?? finalProof.PathProbeId ?? string.Empty,
            finalProof.TargetRoute,
            finalProof.ProtocolVersion,
            finalProof.TargetTransport,
            commitGeneration,
            NormalizeReason(authority.Reason));
        var committedRouteState = routeDecision.State with
        {
            CurrentRouteCommit = routeCommitToken,
            LastRouteCommitGeneration = commitGeneration,
            CurrentRuntimeUnlockAdmission = null,
            CurrentRuntimeUnlockPendingPreCommitProbe = null,
        };
        var publicationRequest = new FileTransferCoordinatorActionRequest(
            FileTransferCoordinatorActionKind.RouteCommitPublication,
            Source: "regular_v4_to_tuna_transition",
            Reason: authority.Reason,
            RuntimeOwner: runtimeOwner,
            Route: committedRouteState.RouteSelection.Route,
            ProtocolVersion: committedRouteState.RouteSelection.ProtocolVersion,
            LiveRouteEpoch: committedRouteState.CurrentLiveRouteEpoch?.EpochId ?? 0,
            LegGeneration: targetLeg.Generation,
            RuntimeGeneration: runtimeOwner.RuntimeGeneration,
            TerminalGeneration: committedRouteState.TerminalGeneration,
            RuntimeUnlockAdmissionToken: authority.RuntimeUnlockAdmission,
            RouteCommitToken: routeCommitToken,
            RuntimeUnlockTransactionGeneration: finalProof.TransactionGeneration,
            RuntimeUnlockOfferGeneration: finalProof.OfferGeneration,
            RuntimeUnlockTunaPathLeaseGeneration: finalProof.TunaPathLeaseGeneration,
            RuntimeUnlockTunaPathLeaseListenerRunId: finalProof.TunaPathLeaseListenerRunId,
            RuntimeUnlockProbeId: finalProof.PathProbeId,
            RuntimeUnlockSamePathTunaProbeAckId: finalProof.SamePathTunaProbeAckId,
            RouteCommitGeneration: commitGeneration,
            TargetRoute: FileTransferRoute.FileTunaV4,
            TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
            TargetTransport: FileTransferTransportKind.Tuna);
        var publicationToken = IssueRoutePublicationToken(
            committedRouteState,
            publicationRequest,
            Math.Max(1, state.LastRoutePublicationGeneration + 1))!.Value;
        committedRouteState = ApplyIssuedRoutePublicationAuthority(
            committedRouteState,
            CreateRoutePublicationDecision(
                committedRouteState,
                FileTransferCoordinatorActionKind.RouteCommitPublication,
                accepted: true,
                rejectionReason: "accepted",
                publicationToken));
        var dispositionCommand = new RegularV4ToTunaRouteTransitionDispositionCommand(
            authority,
            RegularV4ToTunaRouteTransitionDispositionKind.Committed,
            routeCommitToken,
            publicationToken,
            "regular_v4_to_tuna_transition_committed")
        {
            FinalProof = finalProof,
        };
        if (!IsRegularV4ToTunaRouteTransitionDispositionCommandValid(
                dispositionCommand))
        {
            return RegularV4ToTunaDecision(
                state,
                RegularV4ToTunaRouteTransitionDecisionStatus.Rejected,
                "regular_v4_to_tuna_transition_disposition_invalid");
        }

        var retainedDrainReceipt = SnapshotRegularV4ToTunaDrainReceipt(drainReceipt);
        var terminalOperation =
            transitionState.CurrentOperation!.Value with
            {
                State = RegularV4ToTunaRouteTransitionState.Committed,
                DrainReceipt = retainedDrainReceipt,
                FinalProof = finalProof,
                PressureSettlementReceipt = pressureSettlementReceipt,
                RouteCommitToken = routeCommitToken,
                RoutePublicationToken = publicationToken,
                DispositionCommand = dispositionCommand,
            };
        committedRouteState = committedRouteState with
        {
            PersistentState = committedRouteState.PersistentState with
            {
                RegularV4ToTunaRouteTransition = transitionState with
                {
                    CurrentOperation = null,
                    LastTerminalOperation = terminalOperation,
                },
            },
        };
        routeDecision = routeDecision with
        {
            State = committedRouteState,
            RuntimeUnlockCommitAccepted = true,
            RuntimeUnlockCommitRejectedReason = null,
            RouteCommitToken = routeCommitToken,
        };
        return RegularV4ToTunaDecision(
            committedRouteState,
            RegularV4ToTunaRouteTransitionDecisionStatus.Committed,
            "regular_v4_to_tuna_transition_committed",
            authority,
            drainCommand: terminalOperation.DrainCommand,
            dispositionCommand: dispositionCommand,
            routeDecision: routeDecision);
    }

    internal static RegularV4ToTunaRouteTransitionDecision
        InvalidateRegularV4ToTunaRouteTransition(
            FileTransferCoordinatorState state,
            RegularV4ToTunaRouteTransitionAuthority authority,
            bool terminalInvalidation,
            string reason)
    {
        var transitionState = state.PersistentState.RegularV4ToTunaRouteTransition;
        var normalizedReason = NormalizeReason(reason);
        var expectedDispositionKind = terminalInvalidation
            ? RegularV4ToTunaRouteTransitionDispositionKind.TerminalInvalidated
            : RegularV4ToTunaRouteTransitionDispositionKind.RolledBack;
        if (transitionState.LastTerminalOperation is
            {
                State: RegularV4ToTunaRouteTransitionState.Invalidated,
            } terminal &&
            terminal.Authority == authority &&
            terminal.DispositionCommand is { } terminalDisposition)
        {
            if (terminalDisposition.Kind != expectedDispositionKind ||
                !string.Equals(
                    terminalDisposition.Reason,
                    normalizedReason,
                    StringComparison.Ordinal))
            {
                return RegularV4ToTunaDecision(
                    state,
                    RegularV4ToTunaRouteTransitionDecisionStatus.Rejected,
                    "regular_v4_to_tuna_transition_invalidation_duplicate_mismatch");
            }

            return RegularV4ToTunaDecision(
                state,
                RegularV4ToTunaRouteTransitionDecisionStatus.Idempotent,
                "regular_v4_to_tuna_transition_already_invalidated",
                authority,
                drainCommand: terminal.DrainCommand,
                dispositionCommand: terminal.DispositionCommand);
        }

        if (transitionState.CurrentOperation is not { } current ||
            current.Authority != authority)
        {
            return RegularV4ToTunaDecision(
                state,
                RegularV4ToTunaRouteTransitionDecisionStatus.Rejected,
                "regular_v4_to_tuna_transition_authority_not_current");
        }

        if (!terminalInvalidation &&
            !IsRegularV4ToTunaRouteTransitionSourceScopeCurrent(
                state,
                authority,
                out var rejectionReason))
        {
            return RegularV4ToTunaDecision(
                state,
                RegularV4ToTunaRouteTransitionDecisionStatus.Rejected,
                rejectionReason);
        }

        var dispositionCommand = new RegularV4ToTunaRouteTransitionDispositionCommand(
            authority,
            expectedDispositionKind,
            RouteCommitToken: null,
            RoutePublicationToken: null,
            normalizedReason);
        if (!IsRegularV4ToTunaRouteTransitionDispositionCommandValid(
                dispositionCommand))
        {
            return RegularV4ToTunaDecision(
                state,
                RegularV4ToTunaRouteTransitionDecisionStatus.Rejected,
                "regular_v4_to_tuna_transition_disposition_invalid");
        }

        var invalidated = current with
        {
            State = RegularV4ToTunaRouteTransitionState.Invalidated,
            DispositionCommand = dispositionCommand,
        };
        var nextState = state with
        {
            CurrentRuntimeUnlockAdmission = null,
            CurrentRuntimeUnlockPendingPreCommitProbe = null,
            PersistentState = state.PersistentState with
            {
                RegularV4ToTunaRouteTransition = transitionState with
                {
                    CurrentOperation = null,
                    LastTerminalOperation = invalidated,
                },
            },
        };
        return RegularV4ToTunaDecision(
            nextState,
            RegularV4ToTunaRouteTransitionDecisionStatus.Invalidated,
            normalizedReason,
            authority,
            drainCommand: invalidated.DrainCommand,
            dispositionCommand: dispositionCommand);
    }

    internal static RegularV4ToTunaRouteTransitionDecision
        ApplyRegularV4ToTunaRouteTransitionDispositionReceipt(
            FileTransferCoordinatorState state,
            RegularV4ToTunaRouteTransitionDispositionReceipt receipt)
    {
        var transitionState = state.PersistentState.RegularV4ToTunaRouteTransition;
        if (transitionState.LastTerminalOperation is not
            {
                DispositionCommand: { } expectedCommand,
            } terminal ||
            receipt.Command != expectedCommand)
        {
            return RegularV4ToTunaDecision(
                state,
                RegularV4ToTunaRouteTransitionDecisionStatus.Rejected,
                "regular_v4_to_tuna_transition_disposition_receipt_not_current");
        }

        if (terminal.DispositionReceipt is { } appliedReceipt)
        {
            if (receipt == appliedReceipt ||
                IsCanonicalIdempotentRegularV4ToTunaDispositionReceipt(
                    appliedReceipt,
                    receipt))
            {
                return RegularV4ToTunaDecision(
                    state,
                    RegularV4ToTunaRouteTransitionDecisionStatus.Idempotent,
                    "regular_v4_to_tuna_transition_disposition_receipt_already_applied",
                    terminal.Authority,
                    drainCommand: terminal.DrainCommand,
                    dispositionCommand: expectedCommand);
            }

            return RegularV4ToTunaDecision(
                state,
                RegularV4ToTunaRouteTransitionDecisionStatus.Rejected,
                "regular_v4_to_tuna_transition_disposition_receipt_duplicate_mismatch");
        }

        if (!IsCanonicalAcceptedRegularV4ToTunaDispositionReceipt(receipt))
        {
            return RegularV4ToTunaDecision(
                state,
                RegularV4ToTunaRouteTransitionDecisionStatus.Rejected,
                "regular_v4_to_tuna_transition_disposition_receipt_rejected");
        }

        var terminalWithReceipt = terminal with { DispositionReceipt = receipt };
        var nextState = state with
        {
            PersistentState = state.PersistentState with
            {
                RegularV4ToTunaRouteTransition = transitionState with
                {
                    LastTerminalOperation = terminalWithReceipt,
                },
            },
        };
        return RegularV4ToTunaDecision(
            nextState,
            terminal.State == RegularV4ToTunaRouteTransitionState.Committed
                ? RegularV4ToTunaRouteTransitionDecisionStatus.Committed
                : RegularV4ToTunaRouteTransitionDecisionStatus.Invalidated,
            "regular_v4_to_tuna_transition_disposition_receipt_applied",
            terminal.Authority,
            drainCommand: terminal.DrainCommand,
            dispositionCommand: expectedCommand);
    }

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

        var workingState = RequiresClonedCoordinatorGraph(coordinatorEvent.Kind)
            ? CloneCoordinatorStateGraph(state)
            : state;
        var decision = coordinatorEvent.Kind switch
        {
            FileTransferCoordinatorEventKind.StartTransfer =>
                StartTransfer(coordinatorEvent, workingState, reason),
            FileTransferCoordinatorEventKind.TunaActivated =>
                TransitionToRoute(coordinatorEvent, workingState, reason, recoverEpoch: true),
            FileTransferCoordinatorEventKind.TunaLost =>
                TransitionToRoute(coordinatorEvent, workingState, reason, recoverEpoch: false),
            FileTransferCoordinatorEventKind.FallbackCheckpointRequested =>
                MarkFallbackCheckpointRequested(coordinatorEvent, workingState, reason),
            FileTransferCoordinatorEventKind.FallbackCheckpointAccepted =>
                MarkFallbackCheckpointAccepted(coordinatorEvent, workingState, reason),
            FileTransferCoordinatorEventKind.FallbackCheckpointRetired =>
                RetireFallbackCheckpointRequest(coordinatorEvent, workingState, reason),
            FileTransferCoordinatorEventKind.FallbackTailQuiescent =>
                MarkFallbackTailQuiescent(coordinatorEvent, workingState, reason),
            FileTransferCoordinatorEventKind.FallbackTailFailed =>
                MarkFallbackTailFailed(coordinatorEvent, workingState, reason),
            FileTransferCoordinatorEventKind.BridgeRecoveryStarted =>
                MarkCurrentFallbackLegState(workingState, FileTransferLegState.RecoveryActive, reason),
            FileTransferCoordinatorEventKind.BridgeRecoveryCompleted =>
                PassThrough(workingState, reason),
            FileTransferCoordinatorEventKind.BridgeRecoveryExhausted =>
                MarkCurrentFallbackLegState(
                    workingState,
                    FileTransferLegState.BridgeRestartPending,
                    reason),
            FileTransferCoordinatorEventKind.FallbackControlPlaneSendAuthorized =>
                AuthorizeFallbackControlPlaneSend(coordinatorEvent, workingState, reason),
            FileTransferCoordinatorEventKind.FallbackControlPlaneSendCompleted =>
                CompleteFallbackControlPlaneSend(coordinatorEvent, workingState, reason),
            FileTransferCoordinatorEventKind.FallbackControlPlanePressureUpdated =>
                UpdateFallbackControlPlanePressure(coordinatorEvent, workingState, reason),
            FileTransferCoordinatorEventKind.RuntimeUnlockFallbackSurvivalUpdated =>
                UpdateRuntimeUnlockFallbackSurvival(coordinatorEvent, workingState, reason),
            FileTransferCoordinatorEventKind.RuntimeUnlockAdmission =>
                ApplyRuntimeUnlockAdmission(coordinatorEvent, workingState, reason),
            FileTransferCoordinatorEventKind.RuntimeUnlockAdmissionRetired =>
                RetireRuntimeUnlockAdmission(workingState, reason),
            FileTransferCoordinatorEventKind.RuntimeUnlockPreCommitProbePending =>
                ApplyRuntimeUnlockPreCommitProbePending(coordinatorEvent, workingState, reason),
            FileTransferCoordinatorEventKind.RuntimeUnlockPreCommitProbeReplayed =>
                ApplyRuntimeUnlockPreCommitProbeReplayed(coordinatorEvent, workingState, reason),
            FileTransferCoordinatorEventKind.RuntimeUnlockPreCommitProbeRetired =>
                RetireRuntimeUnlockPreCommitProbe(coordinatorEvent, workingState, reason),
            FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested =>
                ApplyRuntimeUnlockCommit(coordinatorEvent, workingState, reason),
            FileTransferCoordinatorEventKind.Terminalized => Terminalize(workingState, reason),
            _ => PassThrough(workingState, reason),
        };
        if (!ReferenceEquals(workingState, state) &&
            (decision.TerminalMutationRejected || decision.MutationRejectedReason is not null))
        {
            decision = decision with
            {
                State = state,
                StartedLiveRouteEpoch = null,
                RecoveredLiveRouteEpoch = null,
                StartedLeg = null,
                FrozenLeg = null,
                AcceptedCheckpointLeg = null,
                MutatedLeg = null,
            };
        }

        return ForwardFallbackRoutePublicationAuthorityAfterLegMutation(state, decision, reason);
    }

    private static bool RequiresClonedCoordinatorGraph(FileTransferCoordinatorEventKind kind)
        => kind is
            FileTransferCoordinatorEventKind.StartTransfer or
            FileTransferCoordinatorEventKind.TunaActivated or
            FileTransferCoordinatorEventKind.TunaLost or
            FileTransferCoordinatorEventKind.FallbackCheckpointRequested or
            FileTransferCoordinatorEventKind.FallbackCheckpointAccepted or
            FileTransferCoordinatorEventKind.FallbackCheckpointRetired or
            FileTransferCoordinatorEventKind.FallbackTailQuiescent or
            FileTransferCoordinatorEventKind.FallbackTailFailed or
            FileTransferCoordinatorEventKind.BridgeRecoveryStarted or
            FileTransferCoordinatorEventKind.BridgeRecoveryExhausted or
            FileTransferCoordinatorEventKind.Terminalized;

    private static FileTransferCoordinatorState CloneCoordinatorStateGraph(
        FileTransferCoordinatorState state)
    {
        var legHistory = state.LegHistory.Select(CloneLeg).ToList();
        FileTransferLeg? currentLeg = null;
        if (state.CurrentLeg is { } sourceCurrentLeg)
        {
            for (var index = 0; index < state.LegHistory.Count; index++)
            {
                if (ReferenceEquals(state.LegHistory[index], sourceCurrentLeg))
                {
                    currentLeg = legHistory[index];
                    break;
                }
            }

            currentLeg ??= legHistory.FirstOrDefault(candidate =>
                candidate.Generation == sourceCurrentLeg.Generation &&
                string.Equals(candidate.LegId, sourceCurrentLeg.LegId, StringComparison.Ordinal));
            if (currentLeg is null)
            {
                currentLeg = CloneLeg(sourceCurrentLeg);
                legHistory.Add(currentLeg);
            }
        }

        return state with
        {
            CurrentLiveRouteEpoch = state.CurrentLiveRouteEpoch is { } epoch
                ? CloneLiveRouteEpoch(epoch)
                : null,
            CurrentLeg = currentLeg,
            LegHistory = legHistory,
        };
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

        if (IsBridgeRecoveryAuthorityAction(request.Kind) &&
            state.CurrentBridgeRecoveryExecutionCommand is { } currentExecution &&
            IsBridgeRecoveryExecutionCommandCurrent(state, currentExecution))
        {
            return ActionDecision(
                state,
                request.Kind,
                accepted: false,
                rejectionReason: "bridge_recovery_execution_pending");
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

        var bridgeRecoveryExecutionCommand = bridgeRecoveryDecision is { } issuedBridgeRecoveryDecision
            ? new FileTransferBridgeRecoveryExecutionCommand(
                issuedBridgeRecoveryDecision,
                recoveryLivenessLease,
                request.CheckpointRequestId)
            : (FileTransferBridgeRecoveryExecutionCommand?)null;
        if (bridgeRecoveryExecutionCommand is { IsDefault: true })
        {
            return ActionDecision(
                state,
                request.Kind,
                accepted: false,
                rejectionReason: "bridge_recovery_execution_command_incomplete");
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
            recoveryLivenessLease,
            bridgeRecoveryExecutionCommand);
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
            bridgeRecoveryExecutionCommand: bridgeRecoveryExecutionCommand,
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

    private static FileTransferLeg CloneLeg(FileTransferLeg leg)
        => new()
        {
            LegId = leg.LegId,
            Generation = leg.Generation,
            RouteSelection = leg.RouteSelection,
            ProtocolVersion = leg.ProtocolVersion,
            LiveRouteEpochId = leg.LiveRouteEpochId,
            TransportEpochId = leg.TransportEpochId,
            StartedUtc = leg.StartedUtc,
            FrozenUtc = leg.FrozenUtc,
            State = leg.State,
            StartCommittedChunkIndex = leg.StartCommittedChunkIndex,
            ProvenCommittedChunkIndex = leg.ProvenCommittedChunkIndex,
            ProvenHighestObservedChunkIndex = leg.ProvenHighestObservedChunkIndex,
            CheckpointRequestId = leg.CheckpointRequestId,
            CheckpointPriority = leg.CheckpointPriority,
            CheckpointRequestedUtc = leg.CheckpointRequestedUtc,
            CheckpointGeneration = leg.CheckpointGeneration,
            BridgeRecoveryGeneration = leg.BridgeRecoveryGeneration,
            CanSendData = leg.CanSendData,
            RuntimeUnlockProbeAllowedAfterTransportEpochAdoption =
                leg.RuntimeUnlockProbeAllowedAfterTransportEpochAdoption,
        };

    private static LiveRouteEpoch CloneLiveRouteEpoch(LiveRouteEpoch epoch)
        => new()
        {
            RoutePublicationToken = epoch.RoutePublicationToken,
            EpochId = epoch.EpochId,
            RouteSelection = epoch.RouteSelection,
            HandoffKind = epoch.HandoffKind,
            TargetTransport = epoch.TargetTransport,
            Reason = epoch.Reason,
            State = epoch.State,
        };

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
            CurrentBridgeRecoveryExecutionCommand = null,
            CurrentBridgeRecoveryExecutionState = null,
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
        if (string.IsNullOrWhiteSpace(request.Reason) ||
            !string.Equals(
                request.Reason,
                NormalizeReason(request.Reason),
                StringComparison.Ordinal))
        {
            rejectionReason = "regular_v4_to_tuna_transition_reason_invalid";
            return false;
        }

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
        var pressureState = state.PersistentState.RegularV4FrontierRepairPressure;
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

        if (pressureState.PendingBinding is { } pending)
        {
            var repeatedCommand = CreateRegularV4FrontierRepairPressureInstallCommand(
                pending,
                authorityRequest);
            if (pending.Authority == issuedAuthority &&
                string.Equals(pending.PressureReason, normalizedReason, StringComparison.Ordinal) &&
                pressureState.PendingInstallCommand == repeatedCommand)
            {
                return RegularV4PressureBindingDecision(
                    state,
                    RegularV4FrontierRepairPressureBindingDecisionStatus.Idempotent,
                    "regular_v4_frontier_repair_pressure_binding_already_pending",
                    pending,
                    pressureState.PendingInstallCommand);
            }

            return RegularV4PressureBindingDecision(
                state,
                RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
                "regular_v4_frontier_repair_pressure_binding_pending_conflict");
        }

        var installed = pressureState.InstalledBinding;
        if (installed is { } current && current.Authority == issuedAuthority)
        {
            var repeatedCommand = CreateRegularV4FrontierRepairPressureInstallCommand(
                current,
                authorityRequest);
            if (pressureState.InstalledInstallCommand == repeatedCommand)
            {
                return RegularV4PressureBindingDecision(
                    state,
                    RegularV4FrontierRepairPressureBindingDecisionStatus.Idempotent,
                    "regular_v4_frontier_repair_pressure_binding_already_installed",
                    current,
                    pressureState.InstalledInstallCommand);
            }

            return RegularV4PressureBindingDecision(
                state,
                RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
                "regular_v4_frontier_repair_pressure_binding_install_command_mismatch");
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

        if (pressureState.RetainedPredecessorBinding is not null)
        {
            return RegularV4PressureBindingDecision(
                state,
                RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
                "regular_v4_frontier_repair_pressure_binding_predecessor_retirement_pending");
        }

        var nextGeneration = Math.Max(
            1,
            pressureState.LastInstallGeneration + 1);
        var binding = new RegularV4FrontierRepairPressureBinding(
            issuedAuthority,
            normalizedReason,
            nextGeneration,
            RegularV4FrontierRepairPressureBindingState.Pending,
            issuedAuthority.PredecessorAuthorityId,
            FinalSettlement: null);
        var installCommand = CreateRegularV4FrontierRepairPressureInstallCommand(
            binding,
            authorityRequest);
        var nextPressureState = pressureState with
        {
            PendingBinding = binding,
            PendingInstallCommand = installCommand,
            LastInstallGeneration = nextGeneration,
        };
        var nextState = state with
        {
            PersistentState = state.PersistentState with
            {
                RegularV4FrontierRepairPressure = nextPressureState,
            },
        };
        return RegularV4PressureBindingDecision(
            nextState,
            RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted,
            "regular_v4_frontier_repair_pressure_binding_staged",
            binding,
            installCommand);
    }

    internal static RegularV4FrontierRepairPressureBindingDecision
        CommitRegularV4FrontierRepairPressureBinding(
            FileTransferCoordinatorState state,
            FileTransferRegularV4FrontierRepairPressureInstallReceipt receipt)
    {
        var pressureState = state.PersistentState.RegularV4FrontierRepairPressure;
        if (pressureState.InstalledBinding is { } installed &&
            installed.Authority == receipt.Authority &&
            installed.InstallGeneration == receipt.InstallGeneration &&
            string.Equals(installed.PressureReason, receipt.PressureReason, StringComparison.Ordinal))
        {
            var exactStoredReceipt =
                pressureState.InstalledInstallReceipt == receipt;
            var canonicalIdempotentReceipt =
                pressureState.InstalledInstallCommand is { } installedCommand &&
                installedCommand.Pressure.RepairAuthority == installed.Authority &&
                installedCommand.InstallGeneration == installed.InstallGeneration &&
                receipt.Status ==
                    FileTransferRegularV4FrontierRepairPressureInstallStatus.Idempotent &&
                string.Equals(
                    receipt.Reason,
                    "regular_v4_frontier_repair_pressure_install_idempotent",
                    StringComparison.Ordinal);
            if (!exactStoredReceipt && !canonicalIdempotentReceipt)
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
                installed,
                pressureState.InstalledInstallCommand);
        }

        if (pressureState.PendingBinding is not { } pending ||
            pressureState.PendingInstallCommand is not { } pendingCommand)
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
            !string.Equals(receipt.PressureReason, pending.PressureReason, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(receipt.Reason) ||
            !string.Equals(receipt.Reason, NormalizeReason(receipt.Reason), StringComparison.Ordinal) ||
            receipt.Authority != pendingCommand.Pressure.RepairAuthority ||
            receipt.InstallGeneration != pendingCommand.InstallGeneration ||
            !string.Equals(
                receipt.PressureReason,
                pendingCommand.Pressure.Reason,
                StringComparison.Ordinal))
        {
            return RegularV4PressureBindingDecision(
                state,
                RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
                "regular_v4_frontier_repair_pressure_binding_install_receipt_mismatch");
        }

        var committed = pending with { State = RegularV4FrontierRepairPressureBindingState.Installed };
        var currentBinding = pressureState.InstalledBinding;
        var replacesCurrent = currentBinding is { } current &&
                              pending.PredecessorAuthorityId == current.Authority.AuthorityId;
        var predecessor = replacesCurrent
            ? currentBinding!.Value with
            {
                State = RegularV4FrontierRepairPressureBindingState.RetainedPredecessor,
            }
            : pressureState.RetainedPredecessorBinding;
        var predecessorCommand = replacesCurrent
            ? pressureState.InstalledInstallCommand
            : pressureState.RetainedPredecessorInstallCommand;
        var predecessorReceipt = replacesCurrent
            ? pressureState.InstalledInstallReceipt
            : pressureState.RetainedPredecessorInstallReceipt;
        var nextPressureState = pressureState with
        {
            PendingBinding = null,
            PendingInstallCommand = null,
            InstalledBinding = committed,
            InstalledInstallCommand = pendingCommand,
            InstalledInstallReceipt = receipt,
            RetainedPredecessorBinding = predecessor,
            RetainedPredecessorInstallCommand = predecessorCommand,
            RetainedPredecessorInstallReceipt = predecessorReceipt,
        };
        var nextState = state with
        {
            PersistentState = state.PersistentState with
            {
                RegularV4FrontierRepairPressure = nextPressureState,
            },
        };
        return RegularV4PressureBindingDecision(
            nextState,
            RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted,
            "regular_v4_frontier_repair_pressure_binding_committed",
            committed,
            pendingCommand);
    }

    internal static RegularV4FrontierRepairPressureBindingDecision
        PrepareRegularV4FrontierRepairPressureSettlement(
            FileTransferCoordinatorState state,
            FileTransferRegularV4FrontierRepairSettlement settlement)
    {
        var pressureState = state.PersistentState.RegularV4FrontierRepairPressure;
        if (pressureState.LastSettlement == settlement)
        {
            return RegularV4PressureBindingDecision(
                state,
                RegularV4FrontierRepairPressureBindingDecisionStatus.Idempotent,
                "regular_v4_frontier_repair_pressure_settlement_already_committed");
        }

        var installed = pressureState.InstalledBinding;
        var retained = pressureState.RetainedPredecessorBinding;
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
            settlementCommand: command);
    }

    internal static RegularV4FrontierRepairPressureBindingDecision
        CommitRegularV4FrontierRepairPressureSettlement(
            FileTransferCoordinatorState state,
            FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt receipt)
    {
        var pressureState = state.PersistentState.RegularV4FrontierRepairPressure;
        var prepared = PrepareRegularV4FrontierRepairPressureSettlement(
            state,
            receipt.Command.Settlement);
        if (prepared.Status == RegularV4FrontierRepairPressureBindingDecisionStatus.Idempotent)
        {
            var committedReceipt = pressureState.LastSettlementApplyReceipt;
            if (committedReceipt is null ||
                !IsExactOrCanonicalIdempotentRegularV4PressureSettlementReceipt(
                    committedReceipt,
                    receipt))
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
        var settlingRetained = pressureState.RetainedPredecessorBinding is { } retained &&
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

        var nextPressureState = settlingRetained
            ? pressureState with
            {
                RetainedPredecessorBinding = null,
                RetainedPredecessorInstallCommand = null,
                RetainedPredecessorInstallReceipt = null,
                LastSettlement = settlement,
                LastSettlementApplyReceipt = receipt,
            }
            : pressureState with
            {
                InstalledBinding = null,
                InstalledInstallCommand = null,
                InstalledInstallReceipt = null,
                PendingBinding = null,
                PendingInstallCommand = null,
                LastSettlement = settlement,
                LastSettlementApplyReceipt = receipt,
            };
        var nextState = state with
        {
            PersistentState = state.PersistentState with
            {
                RegularV4FrontierRepairPressure = nextPressureState,
            },
        };
        return RegularV4PressureBindingDecision(
            nextState,
            RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted,
            "regular_v4_frontier_repair_pressure_settlement_committed");
    }

    private static bool IsExactOrCanonicalIdempotentRegularV4PressureSettlementReceipt(
        FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt committedReceipt,
        FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt candidateReceipt)
        => committedReceipt == candidateReceipt ||
           (committedReceipt.Command == candidateReceipt.Command &&
            candidateReceipt.Status ==
                FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Idempotent &&
            string.Equals(
                candidateReceipt.Reason,
                "regular_v4_frontier_repair_pressure_settlement_apply_idempotent",
                StringComparison.Ordinal) &&
            committedReceipt.CurrentAuthorityCleared == candidateReceipt.CurrentAuthorityCleared &&
            committedReceipt.RetainedPredecessorCleared ==
                candidateReceipt.RetainedPredecessorCleared);

    internal static RegularV4FrontierRepairPressureRecoveryHandoffDecision
        PrepareRegularV4FrontierRepairPressureRecoveryHandoff(
            FileTransferCoordinatorState state,
            FileTransferCoordinatorActionRequest recoveryRequest)
    {
        if (!TryCreateRegularV4FrontierRepairPressureRecoveryHandoffTransition(
                state,
                recoveryRequest,
                out var transition,
                out var rejectionReason))
        {
            return RegularV4PressureRecoveryHandoffDecision(
                state,
                RegularV4FrontierRepairPressureRecoveryHandoffStatus.Rejected,
                rejectionReason);
        }

        return RegularV4PressureRecoveryHandoffDecision(
            state,
            RegularV4FrontierRepairPressureRecoveryHandoffStatus.Prepared,
            "regular_v4_frontier_repair_pressure_recovery_handoff_prepared",
            transition);
    }

    internal static RegularV4FrontierRepairPressureRecoveryHandoffDecision
        CommitRegularV4FrontierRepairPressureRecoveryHandoff(
            FileTransferCoordinatorState state,
            RegularV4FrontierRepairPressureRecoveryHandoffTransition transition,
            FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt receipt)
    {
        if (IsRegularV4FrontierRepairPressureRecoveryHandoffCommitted(state, transition, receipt))
        {
            return RegularV4PressureRecoveryHandoffDecision(
                state,
                RegularV4FrontierRepairPressureRecoveryHandoffStatus.Idempotent,
                "regular_v4_frontier_repair_pressure_recovery_handoff_already_committed",
                transition);
        }

        if (!TryValidateRegularV4FrontierRepairPressureRecoveryHandoffTransition(
                state,
                transition,
                out var rejectionReason))
        {
            return RegularV4PressureRecoveryHandoffDecision(
                state,
                RegularV4FrontierRepairPressureRecoveryHandoffStatus.Rejected,
                rejectionReason);
        }

        if (receipt.Command != transition.SettlementCommand ||
            receipt.Status is not (
                FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Applied or
                FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Idempotent))
        {
            return RegularV4PressureRecoveryHandoffDecision(
                state,
                RegularV4FrontierRepairPressureRecoveryHandoffStatus.Rejected,
                "regular_v4_frontier_repair_pressure_recovery_handoff_apply_receipt_mismatch");
        }

        var pressureCommit = CommitRegularV4FrontierRepairPressureSettlement(state, receipt);
        if (pressureCommit.Status is not RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted)
        {
            return RegularV4PressureRecoveryHandoffDecision(
                state,
                RegularV4FrontierRepairPressureRecoveryHandoffStatus.Rejected,
                pressureCommit.Reason);
        }

        var settledState = pressureCommit.State.CurrentRepairReplayDecision is { } currentRepair &&
                           currentRepair.AuthorityId == transition.PressureBinding.Authority.AuthorityId
            ? pressureCommit.State with { CurrentRepairReplayDecision = null }
            : pressureCommit.State;
        var committedState = ApplyIssuedRecoveryAuthority(
            settledState,
            transition.RecoveryRequest.Kind,
            repairReplayDecision: null,
            transportEpochDecision: null,
            bridgeRecoveryDecision: transition.BridgeRecoveryDecision,
            recoveryLivenessLease: transition.RecoveryLivenessLease,
            bridgeRecoveryExecutionCommand: transition.BridgeRecoveryExecutionCommand);
        var committedPressureState =
            committedState.PersistentState.RegularV4FrontierRepairPressure with
            {
                LastRecoveryHandoff = transition,
            };
        committedState = committedState with
        {
            PersistentState = committedState.PersistentState with
            {
                RegularV4FrontierRepairPressure = committedPressureState,
            },
        };
        return RegularV4PressureRecoveryHandoffDecision(
            committedState,
            RegularV4FrontierRepairPressureRecoveryHandoffStatus.Committed,
            "regular_v4_frontier_repair_pressure_recovery_handoff_committed",
            transition);
    }

    private static bool TryCreateRegularV4FrontierRepairPressureRecoveryHandoffTransition(
        FileTransferCoordinatorState state,
        FileTransferCoordinatorActionRequest recoveryRequest,
        out RegularV4FrontierRepairPressureRecoveryHandoffTransition transition,
        out string rejectionReason)
    {
        transition = default;
        rejectionReason = "accepted";
        var pressureState = state.PersistentState.RegularV4FrontierRepairPressure;
        if (recoveryRequest.Kind != FileTransferCoordinatorActionKind.BridgeReceiveStallRecovery ||
            recoveryRequest.BridgeRecoveryKind != BridgeRecoveryKind.BridgeReceiveStallRecovery)
        {
            rejectionReason = "regular_v4_frontier_repair_pressure_recovery_handoff_action_mismatch";
            return false;
        }

        if (pressureState.PendingBinding is not null)
        {
            rejectionReason = "regular_v4_frontier_repair_pressure_recovery_handoff_binding_pending";
            return false;
        }

        if (pressureState.RetainedPredecessorBinding is not null)
        {
            rejectionReason = "regular_v4_frontier_repair_pressure_recovery_handoff_predecessor_pending";
            return false;
        }

        if (pressureState.InstalledBinding is not { } binding ||
            binding.IsDefault ||
            binding.State != RegularV4FrontierRepairPressureBindingState.Installed)
        {
            rejectionReason = "regular_v4_frontier_repair_pressure_recovery_handoff_binding_missing";
            return false;
        }

        if (state.CurrentRepairReplayDecision is not { } repairDecision ||
            repairDecision.AuthorityId != binding.Authority.AuthorityId ||
            !IsRepairReplayDecisionCurrent(state, repairDecision, out rejectionReason))
        {
            rejectionReason = string.IsNullOrWhiteSpace(rejectionReason)
                ? "regular_v4_frontier_repair_pressure_recovery_handoff_repair_authority_stale"
                : rejectionReason;
            return false;
        }

        var settlement = CreateRegularV4FrontierRepairPressureRecoveryHandoffSettlement(binding.Authority);
        var preparedSettlement = PrepareRegularV4FrontierRepairPressureSettlement(state, settlement);
        if (preparedSettlement.Status != RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted ||
            preparedSettlement.SettlementCommand is not { } settlementCommand)
        {
            rejectionReason = preparedSettlement.Reason;
            return false;
        }

        var projectedPressureState = pressureState with
        {
            PendingBinding = null,
            PendingInstallCommand = null,
            InstalledBinding = null,
            InstalledInstallCommand = null,
            InstalledInstallReceipt = null,
            LastSettlement = settlement,
        };
        var projectedState = state with
        {
            CurrentRepairReplayDecision = null,
            PersistentState = state.PersistentState with
            {
                RegularV4FrontierRepairPressure = projectedPressureState,
            },
        };
        var recoveryDecision = AuthorizeAction(projectedState, recoveryRequest);
        if (!recoveryDecision.Accepted)
        {
            rejectionReason = recoveryDecision.RejectionReason;
            return false;
        }

        if (recoveryDecision.BridgeRecoveryDecision is not { } bridgeRecoveryDecision ||
            bridgeRecoveryDecision.IsDefault ||
            !bridgeRecoveryDecision.Accepted ||
            bridgeRecoveryDecision.RecoveryKind != BridgeRecoveryKind.BridgeReceiveStallRecovery ||
            recoveryDecision.RecoveryLivenessLease is not { } recoveryLivenessLease ||
            recoveryLivenessLease.IsDefault ||
            recoveryLivenessLease.State != RecoveryLivenessLeaseState.Active ||
            recoveryDecision.BridgeRecoveryExecutionCommand is not { } executionCommand ||
            executionCommand.IsDefault)
        {
            rejectionReason = "regular_v4_recovery_authority_incomplete";
            return false;
        }

        transition = new RegularV4FrontierRepairPressureRecoveryHandoffTransition(
            binding,
            settlement,
            settlementCommand,
            recoveryRequest,
            bridgeRecoveryDecision,
            recoveryLivenessLease,
            executionCommand);
        return true;
    }

    private static bool TryValidateRegularV4FrontierRepairPressureRecoveryHandoffTransition(
        FileTransferCoordinatorState state,
        RegularV4FrontierRepairPressureRecoveryHandoffTransition transition,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        var pressureState = state.PersistentState.RegularV4FrontierRepairPressure;
        if (state.IsTerminal)
        {
            rejectionReason = "terminal_state";
            return false;
        }

        if (transition.RecoveryRequest.Kind !=
                FileTransferCoordinatorActionKind.BridgeReceiveStallRecovery ||
            transition.RecoveryRequest.BridgeRecoveryKind !=
                BridgeRecoveryKind.BridgeReceiveStallRecovery)
        {
            rejectionReason = "regular_v4_frontier_repair_pressure_recovery_handoff_action_mismatch";
            return false;
        }

        if (pressureState.PendingBinding is not null)
        {
            rejectionReason = "regular_v4_frontier_repair_pressure_recovery_handoff_binding_pending";
            return false;
        }

        if (pressureState.RetainedPredecessorBinding is not null)
        {
            rejectionReason = "regular_v4_frontier_repair_pressure_recovery_handoff_predecessor_pending";
            return false;
        }

        if (pressureState.InstalledBinding is not { } binding ||
            binding.IsDefault ||
            binding.State != RegularV4FrontierRepairPressureBindingState.Installed ||
            binding != transition.PressureBinding)
        {
            rejectionReason = "regular_v4_frontier_repair_pressure_recovery_handoff_binding_mismatch";
            return false;
        }

        if (state.CurrentRepairReplayDecision is not { } repairDecision ||
            repairDecision.AuthorityId != binding.Authority.AuthorityId ||
            !IsRepairReplayDecisionCurrent(state, repairDecision, out rejectionReason))
        {
            rejectionReason = string.IsNullOrWhiteSpace(rejectionReason)
                ? "regular_v4_frontier_repair_pressure_recovery_handoff_repair_authority_stale"
                : rejectionReason;
            return false;
        }

        var expectedSettlement =
            CreateRegularV4FrontierRepairPressureRecoveryHandoffSettlement(binding.Authority);
        var preparedSettlement = PrepareRegularV4FrontierRepairPressureSettlement(
            state,
            expectedSettlement);
        if (preparedSettlement.Status !=
                RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted ||
            preparedSettlement.SettlementCommand is not { } expectedCommand ||
            expectedSettlement != transition.Settlement ||
            expectedCommand != transition.SettlementCommand)
        {
            rejectionReason = preparedSettlement.Status ==
                    RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted
                ? "regular_v4_frontier_repair_pressure_recovery_handoff_transition_mismatch"
                : preparedSettlement.Reason;
            return false;
        }

        var request = transition.RecoveryRequest;
        var owner = state.CurrentRuntimeOwner;
        var leg = state.CurrentLeg;
        if (owner is null ||
            leg is null ||
            request.RuntimeOwner != owner ||
            request.Route != state.RouteSelection.Route ||
            request.ProtocolVersion != state.RouteSelection.ProtocolVersion ||
            request.LiveRouteEpoch != (state.CurrentLiveRouteEpoch?.EpochId ?? 0) ||
            request.LegGeneration != leg.Generation ||
            request.TransportEpochId != leg.TransportEpochId ||
            request.BridgeRecoveryGeneration != leg.BridgeRecoveryGeneration ||
            (request.CheckpointRequestId is not null &&
             !string.Equals(
                 request.CheckpointRequestId,
                 leg.CheckpointRequestId,
                 StringComparison.Ordinal)))
        {
            rejectionReason = "regular_v4_frontier_repair_pressure_recovery_handoff_recovery_scope_mismatch";
            return false;
        }

        var expectedBridgeDecision = CreateBridgeRecoveryDecision(
            state,
            BridgeRecoveryKind.BridgeReceiveStallRecovery,
            request.Reason,
            Math.Max(1, state.LastBridgeRecoveryDecisionGeneration + 1));
        var expectedLease = IssueRecoveryLivenessLease(
            state,
            Math.Max(1, state.LastRecoveryLivenessLeaseGeneration + 1),
            RecoveryLivenessLeaseState.Active,
            request.Reason);
        var expectedExecutionCommand = expectedBridgeDecision is { } bridgeDecision &&
                                       expectedLease is { } lease
            ? new FileTransferBridgeRecoveryExecutionCommand(
                bridgeDecision,
                lease,
                request.CheckpointRequestId)
            : (FileTransferBridgeRecoveryExecutionCommand?)null;
        if (expectedBridgeDecision is null ||
            expectedLease is null ||
            expectedBridgeDecision.Value != transition.BridgeRecoveryDecision ||
            expectedLease.Value != transition.RecoveryLivenessLease ||
            expectedExecutionCommand != transition.BridgeRecoveryExecutionCommand)
        {
            rejectionReason = "regular_v4_frontier_repair_pressure_recovery_handoff_recovery_authority_mismatch";
            return false;
        }

        return true;
    }

    private static FileTransferRegularV4FrontierRepairSettlement
        CreateRegularV4FrontierRepairPressureRecoveryHandoffSettlement(
            FileTransferRegularV4FrontierRepairAuthority authority)
        => new(
            authority.SessionId,
            authority.TransferId,
            authority.Direction,
            authority.RouteToken,
            authority.ProtocolVersion,
            authority.LiveRouteEpoch,
            authority.RuntimeGeneration,
            authority.TerminalGeneration,
            authority.LegGeneration,
            authority.TransportEpoch,
            authority.BridgeRecoveryGeneration,
            authority.BridgeDecisionGeneration,
            authority.RoutePublicationGeneration,
            authority.RouteCommitGeneration,
            authority.RepairKey,
            authority.FrontierChunkIndex,
            authority.DurableReceivedHighestChunkIndex,
            authority.PendingRepairCount,
            authority.DecisionGeneration,
            "bridge_receive_stall_recovery_pressure_handoff",
            FileTransferRegularV4FrontierRepairSettlementOutcome.StaleRejected,
            authority.TargetTransport)
        {
            AuthorityId = authority.AuthorityId,
        };

    private static bool IsRegularV4FrontierRepairPressureRecoveryHandoffCommitted(
        FileTransferCoordinatorState state,
        RegularV4FrontierRepairPressureRecoveryHandoffTransition transition,
        FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt receipt)
        => state.PersistentState.RegularV4FrontierRepairPressure.InstalledBinding is null &&
           state.PersistentState.RegularV4FrontierRepairPressure.PendingBinding is null &&
           state.PersistentState.RegularV4FrontierRepairPressure.RetainedPredecessorBinding is null &&
           state.PersistentState.RegularV4FrontierRepairPressure.LastRecoveryHandoff == transition &&
           state.PersistentState.RegularV4FrontierRepairPressure.LastSettlement == transition.Settlement &&
           state.PersistentState.RegularV4FrontierRepairPressure.LastSettlementApplyReceipt
               is { } committedReceipt &&
           committedReceipt.Command == transition.SettlementCommand &&
           IsExactOrCanonicalIdempotentRegularV4PressureSettlementReceipt(
               committedReceipt,
               receipt) &&
           state.CurrentBridgeRecoveryDecision == transition.BridgeRecoveryDecision &&
           state.CurrentRecoveryLivenessLease == transition.RecoveryLivenessLease;

    private static RegularV4FrontierRepairPressureRecoveryHandoffDecision
        RegularV4PressureRecoveryHandoffDecision(
            FileTransferCoordinatorState state,
            RegularV4FrontierRepairPressureRecoveryHandoffStatus status,
            string reason,
            RegularV4FrontierRepairPressureRecoveryHandoffTransition? transition = null)
        => new(status, NormalizeReason(reason), transition) { State = state };

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

    private static FileTransferRegularV4FrontierRepairPressureInstallCommand
        CreateRegularV4FrontierRepairPressureInstallCommand(
            RegularV4FrontierRepairPressureBinding binding,
            RegularV4FrontierRepairAuthorityRequest request)
        => new(
            new FileTransferRegularV4ControlFeedbackPressure(
                binding.Authority.SessionId,
                binding.Authority.TransferId,
                CreditExhaustedTimeMs: 0,
                request.FrontierLagChunks is >= 0
                    ? request.FrontierLagChunks.Value
                    : Math.Max(
                        0,
                        binding.Authority.DurableReceivedHighestChunkIndex -
                        binding.Authority.FrontierChunkIndex +
                        1),
                Math.Max(1, request.PendingRepairCount),
                binding.PressureReason,
                binding.Authority),
            binding.InstallGeneration);

    internal static RegularV4FrontierRepairPressureSubstate
        GetRegularV4FrontierRepairPressureSubstate(FileTransferCoordinatorState state)
        => state.PersistentState.RegularV4FrontierRepairPressure;

    internal static bool TryGetPendingRegularV4FrontierRepairPressureInstall(
        FileTransferCoordinatorState state,
        FileTransferAuthorityId authorityId,
        out RegularV4FrontierRepairPressureBinding binding,
        out FileTransferRegularV4FrontierRepairPressureInstallCommand command)
    {
        var pressureState = state.PersistentState.RegularV4FrontierRepairPressure;
        if (!authorityId.IsDefault &&
            pressureState.PendingBinding is { } pending &&
            pressureState.PendingInstallCommand is { } pendingCommand &&
            pending.Authority.AuthorityId == authorityId &&
            pendingCommand.Pressure.RepairAuthority == pending.Authority &&
            pendingCommand.InstallGeneration == pending.InstallGeneration)
        {
            binding = pending;
            command = pendingCommand;
            return true;
        }

        binding = default;
        command = null!;
        return false;
    }

    internal static bool TryGetInstalledRegularV4FrontierRepairPressureInstall(
        FileTransferCoordinatorState state,
        FileTransferAuthorityId authorityId,
        out RegularV4FrontierRepairPressureBinding binding,
        out FileTransferRegularV4FrontierRepairPressureInstallCommand command,
        out FileTransferRegularV4FrontierRepairPressureInstallReceipt receipt)
    {
        var pressureState = state.PersistentState.RegularV4FrontierRepairPressure;
        if (!authorityId.IsDefault &&
            pressureState.InstalledBinding is { } installed &&
            pressureState.InstalledInstallCommand is { } installedCommand &&
            pressureState.InstalledInstallReceipt is { } installedReceipt &&
            installed.Authority.AuthorityId == authorityId &&
            installedCommand.Pressure.RepairAuthority == installed.Authority &&
            installedCommand.InstallGeneration == installed.InstallGeneration &&
            installedReceipt.Authority == installed.Authority &&
            installedReceipt.InstallGeneration == installed.InstallGeneration &&
            string.Equals(
                installedReceipt.PressureReason,
                installed.PressureReason,
                StringComparison.Ordinal))
        {
            binding = installed;
            command = installedCommand;
            receipt = installedReceipt;
            return true;
        }

        binding = default;
        command = null!;
        receipt = null!;
        return false;
    }

    private static RegularV4FrontierRepairPressureBindingDecision RegularV4PressureBindingDecision(
        FileTransferCoordinatorState state,
        RegularV4FrontierRepairPressureBindingDecisionStatus status,
        string reason,
        RegularV4FrontierRepairPressureBinding? binding = null,
        FileTransferRegularV4FrontierRepairPressureInstallCommand? installCommand = null,
        FileTransferRegularV4FrontierRepairPressureSettlementCommand? settlementCommand = null)
        => new(status, NormalizeReason(reason), binding, installCommand, settlementCommand)
        {
            State = state,
        };

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
                currentContract.Authority,
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

    internal static FileTransferBridgeRecoveryExecutionDecision
        ObserveBridgeRecoveryExecutionStart(
            FileTransferCoordinatorState state,
            FileTransferBridgeRecoveryExecutionStartResult result)
    {
        FileTransferBridgeRecoveryExecutionDecision Decision(
            FileTransferBridgeRecoveryExecutionDecisionStatus status,
            string reason,
            FileTransferCoordinatorState? nextState = null)
            => new(status, reason, result.Command)
            {
                State = nextState ?? state,
            };

        if (!result.Accepted)
        {
            return Decision(
                FileTransferBridgeRecoveryExecutionDecisionStatus.Rejected,
                "bridge_recovery_execution_start_rejected");
        }

        if (state.CurrentBridgeRecoveryExecutionCommand is not { } currentCommand ||
            currentCommand != result.Command)
        {
            return Decision(
                FileTransferBridgeRecoveryExecutionDecisionStatus.Rejected,
                "bridge_recovery_execution_command_mismatch");
        }

        if (!IsBridgeRecoveryExecutionCommandCurrent(state, currentCommand))
        {
            return Decision(
                FileTransferBridgeRecoveryExecutionDecisionStatus.Rejected,
                "bridge_recovery_execution_command_stale");
        }

        if (state.CurrentBridgeRecoveryExecutionState ==
            FileTransferBridgeRecoveryExecutionState.Running)
        {
            return result.Status == FileTransferBridgeRecoveryExecutionStartStatus.Idempotent
                ? Decision(
                    FileTransferBridgeRecoveryExecutionDecisionStatus.Idempotent,
                    "bridge_recovery_execution_start_duplicate")
                : Decision(
                    FileTransferBridgeRecoveryExecutionDecisionStatus.Rejected,
                    "bridge_recovery_execution_already_running");
        }

        if (state.CurrentBridgeRecoveryExecutionState !=
            FileTransferBridgeRecoveryExecutionState.Issued)
        {
            return Decision(
                FileTransferBridgeRecoveryExecutionDecisionStatus.Rejected,
                "bridge_recovery_execution_state_invalid");
        }

        return Decision(
            result.Status == FileTransferBridgeRecoveryExecutionStartStatus.Idempotent
                ? FileTransferBridgeRecoveryExecutionDecisionStatus.Idempotent
                : FileTransferBridgeRecoveryExecutionDecisionStatus.Accepted,
            result.Status == FileTransferBridgeRecoveryExecutionStartStatus.Idempotent
                ? "bridge_recovery_execution_start_already_installed"
                : "bridge_recovery_execution_started",
            state with
            {
                CurrentBridgeRecoveryExecutionState =
                    FileTransferBridgeRecoveryExecutionState.Running,
            });
    }

    internal static FileTransferBridgeRecoveryExecutionDecision
        ObserveBridgeRecoveryExecutionReceipt(
            FileTransferCoordinatorState state,
            FileTransferBridgeRecoveryExecutionReceipt receipt)
    {
        FileTransferBridgeRecoveryExecutionDecision Decision(
            FileTransferBridgeRecoveryExecutionDecisionStatus status,
            string reason,
            FileTransferCoordinatorState? nextState = null)
            => new(status, reason, receipt.Command)
            {
                State = nextState ?? state,
            };

        if (receipt.IsDefault)
        {
            return Decision(
                FileTransferBridgeRecoveryExecutionDecisionStatus.Rejected,
                "bridge_recovery_execution_receipt_missing");
        }

        if (state.LastBridgeRecoveryExecutionReceipt is { } lastReceipt)
        {
            if (lastReceipt == receipt)
            {
                return Decision(
                    FileTransferBridgeRecoveryExecutionDecisionStatus.Idempotent,
                    "bridge_recovery_execution_receipt_duplicate");
            }

            if (lastReceipt.Command == receipt.Command)
            {
                return Decision(
                    FileTransferBridgeRecoveryExecutionDecisionStatus.Rejected,
                    "bridge_recovery_execution_receipt_conflict");
            }
        }

        if (state.CurrentBridgeRecoveryExecutionCommand is not { } currentCommand ||
            currentCommand != receipt.Command)
        {
            return Decision(
                FileTransferBridgeRecoveryExecutionDecisionStatus.Rejected,
                "bridge_recovery_execution_receipt_command_mismatch");
        }

        if (!IsBridgeRecoveryExecutionCommandCurrent(state, currentCommand))
        {
            return Decision(
                FileTransferBridgeRecoveryExecutionDecisionStatus.Rejected,
                "bridge_recovery_execution_receipt_command_stale");
        }

        if (receipt.Outcome is not FileTransferBridgeRecoveryExecutionOutcome.Discarded &&
            state.CurrentBridgeRecoveryExecutionState !=
                FileTransferBridgeRecoveryExecutionState.Running)
        {
            return Decision(
                FileTransferBridgeRecoveryExecutionDecisionStatus.Rejected,
                "bridge_recovery_execution_receipt_before_start");
        }

        return Decision(
            FileTransferBridgeRecoveryExecutionDecisionStatus.Accepted,
            "bridge_recovery_execution_receipt_accepted",
            state with
            {
                CurrentBridgeRecoveryExecutionCommand = null,
                CurrentBridgeRecoveryExecutionState = null,
                LastBridgeRecoveryExecutionReceipt = receipt,
            });
    }

    internal static bool IsBridgeRecoveryExecutionCommandCurrent(
        FileTransferCoordinatorState state,
        FileTransferBridgeRecoveryExecutionCommand command)
    {
        if (command.IsDefault ||
            state.IsTerminal ||
            state.CurrentRuntimeOwner is not { } owner ||
            state.CurrentLeg is not { } leg ||
            state.CurrentBridgeRecoveryDecision is not { Accepted: true } currentDecision ||
            currentDecision != command.Decision ||
            command.Decision.RuntimeOwner != owner ||
            command.Decision.CurrentRoute != state.RouteSelection.Route ||
            command.Decision.CurrentProtocolVersion != state.RouteSelection.ProtocolVersion ||
            command.Decision.CurrentLiveRouteEpoch != (state.CurrentLiveRouteEpoch?.EpochId ?? 0) ||
            command.Decision.CurrentRuntimeGeneration != owner.RuntimeGeneration ||
            command.Decision.CurrentTerminalGeneration != state.TerminalGeneration ||
            command.Decision.CurrentLegGeneration != leg.Generation ||
            command.Decision.TransportEpoch != leg.TransportEpochId ||
            command.Decision.BridgeRecoveryGeneration != leg.BridgeRecoveryGeneration ||
            !string.Equals(
                command.CheckpointRequestId,
                leg.CheckpointRequestId,
                StringComparison.Ordinal))
        {
            return false;
        }

        var requiresRecoveryLease =
            state.RouteSelection.Route == FileTransferRoute.RegularNknV4Fast &&
            state.RouteSelection.ProtocolVersion == FileTransferProtocol.ProtocolVersionV4;
        return requiresRecoveryLease
            ? command.RecoveryLivenessLease is { } commandLease &&
              state.CurrentRecoveryLivenessLease is { } currentLease &&
              commandLease == currentLease &&
              RuntimeUnlockRegularV4RecoveryLeaseMatchesCurrentRuntime(state, commandLease) &&
              commandLease.State == RecoveryLivenessLeaseState.Active
            : command.RecoveryLivenessLease is null;
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
            var predecessorReleaseAuthority = stagedContract.RequestActivePredecessorSupersession
                ? activePredecessorContract?.Authority
                : lastSettlement?.Authority;
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
                    predecessorReleaseAuthority,
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

                if (!TryValidateRuntimeUnlockRetryReleaseTuple(
                        releaseKind,
                        releaseAuthorityId,
                        releaseGeneration,
                        releaseOutcome,
                        expectedAuthority: null,
                        out _))
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
                if (!TryValidateRuntimeUnlockRetryReleaseTuple(
                        releaseKind,
                        releaseAuthorityId,
                        releaseGeneration,
                        releaseOutcome,
                        expectedAuthority: null,
                        out _) ||
                    state.CurrentRecoveryLivenessLease is not null ||
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
            releaseKind != RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield)
        {
            rejectionReason = "fallback_paired_tail_yield_pending";
            return false;
        }

        if (!TryValidateRuntimeUnlockRetryReleaseTuple(
                releaseKind,
                releaseAuthorityId,
                releaseGeneration,
                releaseOutcome,
                expectedAuthority: null,
                out rejectionReason) ||
            state.LastFallbackTailYieldDecision is not { Accepted: true } tailYield ||
            tailYield.YieldKind != FallbackTailYieldKind.Quiescent ||
            tailYield.AuthorityId != releaseAuthorityId ||
            releaseGeneration != tailYield.YieldGeneration)
        {
            rejectionReason = "retry_dispatch_predecessor_release_invalid";
            return false;
        }

        if (!state.RuntimeUnlockFallbackSurvivalState.PairedFallbackYieldGranted)
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
        RuntimeUnlockRetryDispatchAuthority? predecessorAuthority,
        RuntimeUnlockRetryReleaseKind releaseKind,
        FileTransferAuthorityId? releaseAuthorityId,
        int releaseGeneration,
        string releaseOutcome,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        if (predecessorAuthority is not { } expectedPredecessor ||
            !TryValidateRuntimeUnlockRetryReleaseTuple(
                releaseKind,
                releaseAuthorityId,
                releaseGeneration,
                releaseOutcome,
                expectedPredecessor,
                out rejectionReason))
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
                    StringComparison.Ordinal))
            {
                rejectionReason = "retry_dispatch_predecessor_recovery_handoff_stale";
                return false;
            }
        }

        return true;
    }

    private static bool TryValidateRuntimeUnlockRetryReleaseTuple(
        RuntimeUnlockRetryReleaseKind releaseKind,
        FileTransferAuthorityId? releaseAuthorityId,
        int releaseGeneration,
        string releaseOutcome,
        RuntimeUnlockRetryDispatchAuthority? expectedAuthority,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        if (releaseAuthorityId is not { IsDefault: false } ||
            releaseGeneration <= 0 ||
            (expectedAuthority is { } predecessor &&
             (predecessor.ReleaseKind != releaseKind ||
              predecessor.ReleaseAuthorityId != releaseAuthorityId ||
              predecessor.ReleaseGeneration != releaseGeneration ||
              !string.Equals(
                  predecessor.ReleaseOutcome,
                  releaseOutcome,
                  StringComparison.Ordinal))))
        {
            rejectionReason = "retry_dispatch_predecessor_release_invalid";
            return false;
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
                nameof(FallbackTailYieldKind.Quiescent),
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
        if (state.PersistentState.RegularV4ToTunaRouteTransition.CurrentOperation
            is not null)
        {
            return MutationRejected(
                state,
                reason,
                "regular_v4_to_tuna_transition_invalidation_required");
        }

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

    internal static RuntimeUnlockUncommittedTunaFallbackHandoffTransitionDecision
        PrepareRuntimeUnlockUncommittedTunaFallbackHandoff(
            FileTransferCoordinatorState state,
            FileTransferCoordinatorEvent coordinatorEvent)
    {
        if (!TryCreateRuntimeUnlockUncommittedTunaFallbackHandoffTransition(
                state,
                coordinatorEvent,
                out var transition,
                out var rejectionReason))
        {
            return RuntimeUnlockUncommittedTunaFallbackDecision(
                state,
                RuntimeUnlockUncommittedTunaFallbackHandoffTransitionStatus.Rejected,
                rejectionReason);
        }

        var projectedState = state;
        if (transition.PressureSettlementCommand is not null)
        {
            var pressureState = state.PersistentState.RegularV4FrontierRepairPressure with
            {
                PendingBinding = null,
                PendingInstallCommand = null,
                InstalledBinding = null,
                InstalledInstallCommand = null,
                InstalledInstallReceipt = null,
                LastSettlement = transition.PressureSettlement,
            };
            projectedState = state with
            {
                PersistentState = state.PersistentState with
                {
                    RegularV4FrontierRepairPressure = pressureState,
                },
            };
        }
        if (!TryCreateRuntimeUnlockUncommittedTunaFallbackRouteDecision(
                projectedState,
                transition,
                out var routeDecision,
                out rejectionReason))
        {
            return RuntimeUnlockUncommittedTunaFallbackDecision(
                state,
                RuntimeUnlockUncommittedTunaFallbackHandoffTransitionStatus.Rejected,
                rejectionReason);
        }

        return RuntimeUnlockUncommittedTunaFallbackDecision(
            state,
            RuntimeUnlockUncommittedTunaFallbackHandoffTransitionStatus.Prepared,
            "uncommitted_tuna_fallback_handoff_prepared",
            transition,
            routeDecision);
    }

    internal static RuntimeUnlockUncommittedTunaFallbackHandoffTransitionDecision
        CommitRuntimeUnlockUncommittedTunaFallbackHandoff(
            FileTransferCoordinatorState state,
            RuntimeUnlockUncommittedTunaFallbackHandoffTransition transition,
            RuntimeUnlockUncommittedTunaFallbackHandoff currentHandoff,
            RuntimeUnlockUncommittedTunaFallbackAdoptionStageReceipt adoptionStageReceipt,
            RuntimeUnlockUncommittedTunaFallbackAdoptionPreparationReceipt preparationReceipt)
    {
        if (!IsRuntimeUnlockUncommittedTunaFallbackAdoptionStageReceiptValid(
                transition.Handoff,
                adoptionStageReceipt) ||
            !IsRuntimeUnlockUncommittedTunaFallbackAdoptionPreparationReceiptValid(
                transition,
                adoptionStageReceipt,
                preparationReceipt))
        {
            return RuntimeUnlockUncommittedTunaFallbackDecision(
                state,
                RuntimeUnlockUncommittedTunaFallbackHandoffTransitionStatus.Rejected,
                "uncommitted_tuna_fallback_adoption_stage_receipt_mismatch");
        }

        var pressureReceipt = preparationReceipt.PressureSettlementReceipt;

        if (state.LastRuntimeUnlockUncommittedTunaFallbackHandoff == transition)
        {
            if (currentHandoff != transition.Handoff ||
                !IsRuntimeUnlockUncommittedTunaFallbackHandoffCommitted(
                    state,
                    transition,
                    pressureReceipt))
            {
                return RuntimeUnlockUncommittedTunaFallbackDecision(
                    state,
                    RuntimeUnlockUncommittedTunaFallbackHandoffTransitionStatus.Rejected,
                    "uncommitted_tuna_fallback_handoff_duplicate_mismatch");
            }

            return RuntimeUnlockUncommittedTunaFallbackDecision(
                state,
                RuntimeUnlockUncommittedTunaFallbackHandoffTransitionStatus.Idempotent,
                "uncommitted_tuna_fallback_handoff_already_committed",
                transition);
        }

        var currentEvent = transition.CoordinatorEvent with
        {
            RuntimeUnlockUncommittedTunaFallbackHandoff = currentHandoff,
        };
        if (!TryCreateRuntimeUnlockUncommittedTunaFallbackHandoffTransition(
                state,
                currentEvent,
                out var expectedTransition,
                out var rejectionReason) ||
            currentHandoff != transition.Handoff ||
            expectedTransition != transition)
        {
            return RuntimeUnlockUncommittedTunaFallbackDecision(
                state,
                RuntimeUnlockUncommittedTunaFallbackHandoffTransitionStatus.Rejected,
                string.Equals(rejectionReason, "accepted", StringComparison.Ordinal)
                    ? "uncommitted_tuna_fallback_handoff_transition_mismatch"
                    : rejectionReason);
        }

        var projectedState = state;
        if (transition.PressureSettlementCommand is { } pressureCommand)
        {
            if (pressureReceipt is null ||
                pressureReceipt.Command != pressureCommand ||
                pressureReceipt.Status is not (
                    FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Applied or
                    FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Idempotent) ||
                string.IsNullOrWhiteSpace(pressureReceipt.Reason) ||
                !string.Equals(
                    pressureReceipt.Reason,
                    NormalizeReason(pressureReceipt.Reason),
                    StringComparison.Ordinal) ||
                !pressureReceipt.CurrentAuthorityCleared ||
                pressureReceipt.RetainedPredecessorCleared)
            {
                return RuntimeUnlockUncommittedTunaFallbackDecision(
                    state,
                    RuntimeUnlockUncommittedTunaFallbackHandoffTransitionStatus.Rejected,
                    "uncommitted_tuna_fallback_pressure_apply_receipt_mismatch");
            }

            var pressureCommit = CommitRegularV4FrontierRepairPressureSettlement(
                state,
                pressureReceipt);
            if (pressureCommit.Status != RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted)
            {
                return RuntimeUnlockUncommittedTunaFallbackDecision(
                    state,
                    RuntimeUnlockUncommittedTunaFallbackHandoffTransitionStatus.Rejected,
                    pressureCommit.Reason);
            }

            projectedState = pressureCommit.State;
        }
        else if (pressureReceipt is not null)
        {
            return RuntimeUnlockUncommittedTunaFallbackDecision(
                state,
                RuntimeUnlockUncommittedTunaFallbackHandoffTransitionStatus.Rejected,
                "uncommitted_tuna_fallback_pressure_apply_receipt_unexpected");
        }

        if (!TryCreateRuntimeUnlockUncommittedTunaFallbackRouteDecision(
                projectedState,
                transition,
                out var routeDecision,
                out var routeRejectionReason))
        {
            return RuntimeUnlockUncommittedTunaFallbackDecision(
                state,
                RuntimeUnlockUncommittedTunaFallbackHandoffTransitionStatus.Rejected,
                routeRejectionReason);
        }

        if (!IsRuntimeUnlockUncommittedTunaFallbackProjectedTargetValid(
                routeDecision,
                adoptionStageReceipt.Stage))
        {
            return RuntimeUnlockUncommittedTunaFallbackDecision(
                state,
                RuntimeUnlockUncommittedTunaFallbackHandoffTransitionStatus.Rejected,
                "uncommitted_tuna_fallback_projected_target_mismatch");
        }

        var committedState = routeDecision.State;
        return RuntimeUnlockUncommittedTunaFallbackDecision(
            committedState,
            RuntimeUnlockUncommittedTunaFallbackHandoffTransitionStatus.Committed,
            "uncommitted_tuna_fallback_handoff_committed",
            transition,
            routeDecision);
    }

    private static bool TryCreateRuntimeUnlockUncommittedTunaFallbackRouteDecision(
        FileTransferCoordinatorState projectedState,
        RuntimeUnlockUncommittedTunaFallbackHandoffTransition transition,
        out FileTransferCoordinatorDecision routeDecision,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        var routeTransitionState =
            projectedState.PersistentState.RegularV4ToTunaRouteTransition;
        if (transition.Handoff.RouteTransitionAuthority is not { } transitionAuthority ||
            transition.Handoff.PhysicalDrainReceipt is not { } drainReceipt ||
            routeTransitionState.CurrentOperation is not
            {
                State: RegularV4ToTunaRouteTransitionState.Draining,
            } currentOperation ||
            currentOperation.Authority != transitionAuthority ||
            !TryValidateRegularV4ToTunaDrainReceipt(
                projectedState,
                transitionAuthority,
                drainReceipt,
                out rejectionReason))
        {
            routeDecision = Decision(projectedState, transition.CoordinatorEvent.Reason);
            if (string.Equals(rejectionReason, "accepted", StringComparison.Ordinal))
            {
                rejectionReason =
                    "uncommitted_tuna_fallback_route_transition_authority_mismatch";
            }

            return false;
        }

        projectedState = CloneCoordinatorStateGraph(projectedState);
        routeDecision = TransitionToRoute(
            transition.CoordinatorEvent,
            projectedState,
            NormalizeReason(transition.CoordinatorEvent.Reason),
            recoverEpoch: false);
        if (routeDecision.MutationRejectedReason is { } routeRejectionReason)
        {
            rejectionReason = routeRejectionReason;
            return false;
        }

        var routeState = routeDecision.State;
        var runtimeOwner = routeState.CurrentRuntimeOwner!.Value;
        var leg = routeState.CurrentLeg!;
        var publicationRequest = new FileTransferCoordinatorActionRequest(
            FileTransferCoordinatorActionKind.RoutePublication,
            Source: "uncommitted_tuna_fallback_handoff",
            Reason: transition.Handoff.Reason,
            RuntimeOwner: runtimeOwner,
            Route: routeState.RouteSelection.Route,
            ProtocolVersion: routeState.RouteSelection.ProtocolVersion,
            LiveRouteEpoch: routeState.CurrentLiveRouteEpoch?.EpochId ?? 0,
            LegGeneration: leg.Generation,
            RuntimeGeneration: runtimeOwner.RuntimeGeneration,
            TerminalGeneration: routeState.TerminalGeneration,
            TargetRoute: FileTransferRoute.PostTunaFallbackV6,
            TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV6,
            TargetTransport: FileTransferTransportKind.RegularNkn);
        var publicationToken = IssueRoutePublicationToken(
            routeState,
            publicationRequest,
            Math.Max(1, routeState.LastRoutePublicationGeneration + 1))!.Value;
        var committedState = ApplyIssuedRoutePublicationAuthority(
            routeState,
            CreateRoutePublicationDecision(
                routeState,
                FileTransferCoordinatorActionKind.RoutePublication,
                accepted: true,
                rejectionReason: "accepted",
                publicationToken)) with
        {
            LastRuntimeUnlockUncommittedTunaFallbackHandoff = transition,
        };
        var supersededDisposition =
            new RegularV4ToTunaRouteTransitionDispositionCommand(
                transitionAuthority,
                RegularV4ToTunaRouteTransitionDispositionKind.Superseded,
                RouteCommitToken: null,
                RoutePublicationToken: publicationToken,
                "regular_v4_to_tuna_transition_superseded_by_fallback");
        if (!IsRegularV4ToTunaRouteTransitionDispositionCommandValid(
                supersededDisposition))
        {
            rejectionReason =
                "uncommitted_tuna_fallback_route_transition_disposition_invalid";
            return false;
        }

        var supersededOperation = currentOperation with
        {
            State = RegularV4ToTunaRouteTransitionState.Invalidated,
            DrainReceipt = SnapshotRegularV4ToTunaDrainReceipt(drainReceipt),
            DispositionCommand = supersededDisposition,
        };
        committedState = committedState with
        {
            PersistentState = committedState.PersistentState with
            {
                RegularV4ToTunaRouteTransition = routeTransitionState with
                {
                    CurrentOperation = null,
                    LastTerminalOperation = supersededOperation,
                },
            },
        };
        routeDecision = routeDecision with { State = committedState };
        rejectionReason = "accepted";
        return true;
    }

    private static bool IsRuntimeUnlockUncommittedTunaFallbackAdoptionStageReceiptValid(
        RuntimeUnlockUncommittedTunaFallbackHandoff handoff,
        RuntimeUnlockUncommittedTunaFallbackAdoptionStageReceipt receipt)
    {
        var stage = receipt.Stage;
        return receipt.Status is (
                   RuntimeUnlockUncommittedTunaFallbackAdoptionStageStatus.Staged or
                   RuntimeUnlockUncommittedTunaFallbackAdoptionStageStatus.Idempotent) &&
               receipt.StageGeneration > 0 &&
               !string.IsNullOrWhiteSpace(receipt.Reason) &&
               string.Equals(receipt.Reason, NormalizeReason(receipt.Reason), StringComparison.Ordinal) &&
               stage.DataSession is not null &&
               stage.DataSessionGeneration == handoff.SourceDataSessionGeneration &&
               stage.SourceRuntimeOwner == handoff.SourceRuntimeOwner &&
               stage.SourceRoutePublication == handoff.SourceRoutePublication &&
               stage.RuntimeUnlockAdmission == handoff.RuntimeUnlockAdmission &&
               stage.PendingPreCommitProbe == handoff.PendingPreCommitProbe &&
               stage.PreCommitSnapshot == handoff.PreCommitSnapshot &&
               stage.SourceLegGeneration == handoff.SourceLegGeneration &&
               stage.SourceTransportEpoch == handoff.SourceTransportEpoch &&
               stage.SourceBridgeRecoveryGeneration == handoff.SourceBridgeRecoveryGeneration &&
               stage.HandoffGeneration == handoff.HandoffGeneration &&
               stage.DrainGeneration == handoff.DrainGeneration &&
               stage.CompletedDrainGeneration == handoff.CompletedDrainGeneration &&
               stage.TargetTransportEpoch == handoff.ProofTransportEpoch &&
               stage.TargetBridgeRecoveryGeneration > handoff.SourceBridgeRecoveryGeneration &&
               stage.PeerProofEvidence == handoff.PeerProofEvidence;
    }

    private static bool IsRuntimeUnlockUncommittedTunaFallbackAdoptionPreparationReceiptValid(
        RuntimeUnlockUncommittedTunaFallbackHandoffTransition transition,
        RuntimeUnlockUncommittedTunaFallbackAdoptionStageReceipt stageReceipt,
        RuntimeUnlockUncommittedTunaFallbackAdoptionPreparationReceipt receipt)
    {
        var stage = stageReceipt.Stage;
        var command = receipt.Command;
        return receipt.Status is (
                   RuntimeUnlockUncommittedTunaFallbackAdoptionPreparationStatus.Prepared or
                   RuntimeUnlockUncommittedTunaFallbackAdoptionPreparationStatus.Idempotent) &&
               !string.IsNullOrWhiteSpace(receipt.Reason) &&
               string.Equals(receipt.Reason, NormalizeReason(receipt.Reason), StringComparison.Ordinal) &&
               command.DataSession is not null &&
               ReferenceEquals(command.DataSession, stage.DataSession) &&
               command.DataSessionGeneration == stage.DataSessionGeneration &&
               command.StageGeneration == stageReceipt.StageGeneration &&
               command.RuntimeOwner == stage.TargetRuntimeOwner &&
               command.RoutePublication == stage.TargetRoutePublication &&
               command.TransportEpoch == stage.TargetTransportEpoch &&
               command.BridgeRecoveryGeneration == stage.TargetBridgeRecoveryGeneration &&
               command.HandoffGeneration == stage.HandoffGeneration &&
               command.DrainGeneration == stage.DrainGeneration &&
               command.PeerProofEvidence == stage.PeerProofEvidence &&
               command.PressureSettlementCommand == transition.PressureSettlementCommand &&
               command.PressureSettlementCommand == stage.PressureSettlementCommand &&
               command.SourceAvailabilityGeneration == stageReceipt.AvailabilityGeneration &&
               command.SourceActivationPaused == stageReceipt.ActivationPaused &&
               string.Equals(
                   command.SourceAvailabilityReason,
                   stageReceipt.AvailabilityReason,
                   StringComparison.Ordinal);
    }

    private static bool IsRuntimeUnlockUncommittedTunaFallbackProjectedTargetValid(
        FileTransferCoordinatorDecision routeDecision,
        RuntimeUnlockUncommittedTunaFallbackAdoptionStage stage)
        => routeDecision.State.CurrentRuntimeOwner == stage.TargetRuntimeOwner &&
           routeDecision.State.CurrentRoutePublicationToken == stage.TargetRoutePublication &&
           routeDecision.State.CurrentLeg is { } leg &&
           leg.Generation == stage.TargetRoutePublication.LegGeneration &&
           leg.TransportEpochId == stage.TargetTransportEpoch &&
           leg.BridgeRecoveryGeneration == stage.TargetBridgeRecoveryGeneration &&
           leg.State == FileTransferLegState.CheckpointPending &&
           !leg.CanSendData &&
           routeDecision.State.RouteSelection.Route == FileTransferRoute.PostTunaFallbackV6 &&
           routeDecision.State.RouteSelection.ProtocolVersion == FileTransferProtocol.ProtocolVersionV6;

    private static bool TryCreateRuntimeUnlockUncommittedTunaFallbackHandoffTransition(
        FileTransferCoordinatorState state,
        FileTransferCoordinatorEvent coordinatorEvent,
        out RuntimeUnlockUncommittedTunaFallbackHandoffTransition transition,
        out string rejectionReason)
    {
        transition = default;
        rejectionReason = "accepted";
        var pressureState = state.PersistentState.RegularV4FrontierRepairPressure;
        if (coordinatorEvent.RuntimeUnlockUncommittedTunaFallbackHandoff is not { } handoff ||
            !IsRuntimeUnlockUncommittedTunaFallbackHandoffCurrent(
                state,
                coordinatorEvent,
                handoff,
                out rejectionReason))
        {
            if (string.Equals(rejectionReason, "accepted", StringComparison.Ordinal))
            {
                rejectionReason = "uncommitted_tuna_fallback_handoff_missing";
            }

            return false;
        }

        if (pressureState.PendingBinding is not null)
        {
            rejectionReason = "uncommitted_tuna_fallback_pressure_binding_pending";
            return false;
        }

        if (pressureState.RetainedPredecessorBinding is not null)
        {
            rejectionReason = "uncommitted_tuna_fallback_pressure_predecessor_pending";
            return false;
        }

        RegularV4FrontierRepairPressureBinding? pressureBinding = null;
        FileTransferRegularV4FrontierRepairSettlement? pressureSettlement = null;
        FileTransferRegularV4FrontierRepairPressureSettlementCommand? pressureCommand = null;
        if (pressureState.InstalledBinding is { } installed)
        {
            if (installed.IsDefault ||
                installed.State != RegularV4FrontierRepairPressureBindingState.Installed)
            {
                rejectionReason = "uncommitted_tuna_fallback_pressure_binding_invalid";
                return false;
            }

            pressureSettlement = CreateRuntimeUnlockUncommittedTunaFallbackPressureSettlement(
                installed.Authority);
            var preparedPressure = PrepareRegularV4FrontierRepairPressureSettlement(
                state,
                pressureSettlement);
            if (preparedPressure.Status !=
                    RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted ||
                preparedPressure.SettlementCommand is not { } settlementCommand)
            {
                rejectionReason = preparedPressure.Reason;
                return false;
            }

            pressureBinding = installed;
            pressureCommand = settlementCommand;
        }

        transition = new RuntimeUnlockUncommittedTunaFallbackHandoffTransition(
            coordinatorEvent,
            handoff,
            pressureBinding,
            pressureSettlement,
            pressureCommand);
        return true;
    }

    private static FileTransferRegularV4FrontierRepairSettlement
        CreateRuntimeUnlockUncommittedTunaFallbackPressureSettlement(
            FileTransferRegularV4FrontierRepairAuthority authority)
        => new(
            authority.SessionId,
            authority.TransferId,
            authority.Direction,
            authority.RouteToken,
            authority.ProtocolVersion,
            authority.LiveRouteEpoch,
            authority.RuntimeGeneration,
            authority.TerminalGeneration,
            authority.LegGeneration,
            authority.TransportEpoch,
            authority.BridgeRecoveryGeneration,
            authority.BridgeDecisionGeneration,
            authority.RoutePublicationGeneration,
            authority.RouteCommitGeneration,
            authority.RepairKey,
            authority.FrontierChunkIndex,
            authority.DurableReceivedHighestChunkIndex,
            authority.PendingRepairCount,
            authority.DecisionGeneration,
            "uncommitted_tuna_fallback_pressure_handoff",
            FileTransferRegularV4FrontierRepairSettlementOutcome.StaleRejected,
            authority.TargetTransport)
        {
            AuthorityId = authority.AuthorityId,
        };

    private static bool IsRuntimeUnlockUncommittedTunaFallbackHandoffCommitted(
        FileTransferCoordinatorState state,
        RuntimeUnlockUncommittedTunaFallbackHandoffTransition transition,
        FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt? pressureReceipt)
    {
        var pressureState = state.PersistentState.RegularV4FrontierRepairPressure;
        var routeTransitionState =
            state.PersistentState.RegularV4ToTunaRouteTransition;
        if (state.IsTerminal ||
            state.RouteSelection.Route != FileTransferRoute.PostTunaFallbackV6 ||
            state.RouteSelection.ProtocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            state.CurrentRuntimeOwner is not { Route: FileTransferRoute.PostTunaFallbackV6 } ||
            state.CurrentRouteCommit is not null ||
            state.CurrentRuntimeUnlockAdmission is not null ||
            state.CurrentRuntimeUnlockPendingPreCommitProbe is not null ||
            state.CurrentRoutePublicationToken is not { } publication ||
            !IsRoutePublicationTokenCurrent(state, publication, out _) ||
            pressureState.PendingBinding is not null ||
            pressureState.InstalledBinding is not null ||
            pressureState.RetainedPredecessorBinding is not null ||
            transition.Handoff.RouteTransitionAuthority is not { } transitionAuthority ||
            transition.Handoff.PhysicalDrainReceipt is not { } drainReceipt ||
            routeTransitionState.CurrentOperation is not null ||
            routeTransitionState.LastTerminalOperation is not
            {
                State: RegularV4ToTunaRouteTransitionState.Invalidated,
                DispositionCommand:
                {
                    Kind: RegularV4ToTunaRouteTransitionDispositionKind.Superseded,
                },
            } terminalTransition ||
            terminalTransition.Authority != transitionAuthority ||
            terminalTransition.DrainReceipt is not { } committedDrainReceipt ||
            !IsExactOrCanonicalIdempotentRegularV4ToTunaDrainReceipt(
                committedDrainReceipt,
                drainReceipt))
        {
            return false;
        }

        if (transition.PressureSettlementCommand is null)
        {
            return pressureReceipt is null;
        }

        return pressureReceipt is not null &&
               pressureState.LastSettlement == transition.PressureSettlement &&
               pressureState.LastSettlementApplyReceipt is { } committedReceipt &&
               committedReceipt.Command == transition.PressureSettlementCommand &&
               committedReceipt.CurrentAuthorityCleared &&
               !committedReceipt.RetainedPredecessorCleared &&
               IsExactOrCanonicalIdempotentRegularV4PressureSettlementReceipt(
                   committedReceipt,
                   pressureReceipt);
    }

    private static RuntimeUnlockUncommittedTunaFallbackHandoffTransitionDecision
        RuntimeUnlockUncommittedTunaFallbackDecision(
            FileTransferCoordinatorState state,
            RuntimeUnlockUncommittedTunaFallbackHandoffTransitionStatus status,
            string reason,
            RuntimeUnlockUncommittedTunaFallbackHandoffTransition? transition = null,
            FileTransferCoordinatorDecision? routeDecision = null)
        => new(status, NormalizeReason(reason), transition, routeDecision) { State = state };

    private static FileTransferCoordinatorDecision TransitionToRoute(
        FileTransferCoordinatorEvent coordinatorEvent,
        FileTransferCoordinatorState state,
        string reason,
        bool recoverEpoch)
    {
        if (state.PersistentState.RegularV4ToTunaRouteTransition.CurrentOperation
                is not null &&
            coordinatorEvent.RuntimeUnlockUncommittedTunaFallbackHandoff is null)
        {
            return MutationRejected(
                state,
                reason,
                "regular_v4_to_tuna_transition_commit_required");
        }

        if (coordinatorEvent.RuntimeUnlockUncommittedTunaFallbackHandoff is { } fallbackHandoff &&
            !IsRuntimeUnlockUncommittedTunaFallbackHandoffCurrent(
                state,
                coordinatorEvent,
                fallbackHandoff,
                out var fallbackHandoffRejectionReason))
        {
            return MutationRejected(state, reason, fallbackHandoffRejectionReason);
        }

        if (HasRegularV4FrontierRepairPressureBinding(state))
        {
            return MutationRejected(
                state,
                reason,
                "regular_v4_frontier_repair_pressure_retirement_required");
        }

        var legHistory = state.LegHistory.Select(CloneLeg).ToList();
        var currentLeg = state.CurrentLeg;
        var clonedCurrentLeg = currentLeg is null
            ? null
            : legHistory.FirstOrDefault(candidate =>
                  candidate.Generation == currentLeg.Generation &&
                  string.Equals(candidate.LegId, currentLeg.LegId, StringComparison.Ordinal)) ??
              CloneLeg(currentLeg);
        var frozenLeg = FreezeLeg(clonedCurrentLeg, DateTimeOffset.UtcNow);
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

    private static bool IsRuntimeUnlockUncommittedTunaFallbackHandoffCurrent(
        FileTransferCoordinatorState state,
        FileTransferCoordinatorEvent coordinatorEvent,
        RuntimeUnlockUncommittedTunaFallbackHandoff handoff,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        if (coordinatorEvent.Kind != FileTransferCoordinatorEventKind.TunaLost ||
            coordinatorEvent.RouteSelection.Route != FileTransferRoute.PostTunaFallbackV6 ||
            coordinatorEvent.RouteSelection.ProtocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            coordinatorEvent.HandoffKind != FileTransferTransportHandoffKind.TunaToNormalFallback ||
            coordinatorEvent.TargetTransport != FileTransferTransportKind.RegularNkn ||
            coordinatorEvent.LegState != FileTransferLegState.CheckpointPending ||
            coordinatorEvent.CanSendData)
        {
            rejectionReason = "uncommitted_tuna_fallback_target_invalid";
            return false;
        }

        if (state.IsTerminal ||
            state.RouteSelection.Route != FileTransferRoute.RegularNknV4Fast ||
            state.RouteSelection.ProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
            state.CurrentRuntimeOwner != handoff.SourceRuntimeOwner ||
            state.CurrentRoutePublicationToken != handoff.SourceRoutePublication ||
            state.CurrentRuntimeUnlockAdmission != handoff.RuntimeUnlockAdmission ||
            state.CurrentRuntimeUnlockPendingPreCommitProbe != handoff.PendingPreCommitProbe ||
            state.CurrentRouteCommit is not null ||
            !IsRuntimeOwnerCurrent(state, handoff.SourceRuntimeOwner, out _) ||
            !IsRoutePublicationTokenCurrent(state, handoff.SourceRoutePublication, out _) ||
            !IsRuntimeUnlockAdmissionTokenCurrent(state, handoff.RuntimeUnlockAdmission, out _) ||
            !IsRuntimeUnlockPendingPreCommitProbeRequestCurrent(
                state,
                handoff.PendingPreCommitProbe,
                out _) ||
            state.TerminalGeneration != handoff.SourceTerminalGeneration ||
            state.CurrentLeg is not { } leg ||
            leg.Generation != handoff.SourceLegGeneration ||
            leg.TransportEpochId != handoff.SourceTransportEpoch ||
            leg.BridgeRecoveryGeneration != handoff.SourceBridgeRecoveryGeneration)
        {
            rejectionReason = "uncommitted_tuna_fallback_source_scope_stale";
            return false;
        }

        var snapshot = handoff.PreCommitSnapshot;
        var admission = handoff.RuntimeUnlockAdmission;
        var pendingPreCommit = handoff.PendingPreCommitProbe;
        var peerProof = handoff.PeerProofEvidence;
        if (handoff.RouteTransitionAuthority is not { } transitionAuthority ||
            handoff.PhysicalDrainReceipt is not { } physicalDrainReceipt ||
            transitionAuthority.SourceRuntimeOwner != handoff.SourceRuntimeOwner ||
            transitionAuthority.SourceRoutePublication != handoff.SourceRoutePublication ||
            transitionAuthority.RuntimeUnlockAdmission != admission ||
            transitionAuthority.PendingPreCommitProbe != pendingPreCommit ||
            transitionAuthority.DataSessionGeneration !=
                handoff.SourceDataSessionGeneration ||
            transitionAuthority.SourceTerminalGeneration !=
                handoff.SourceTerminalGeneration ||
            transitionAuthority.SourceLegGeneration != handoff.SourceLegGeneration ||
            transitionAuthority.SourceTransportEpoch != handoff.SourceTransportEpoch ||
            transitionAuthority.SourceBridgeRecoveryGeneration !=
                handoff.SourceBridgeRecoveryGeneration ||
            transitionAuthority.LaunchGeneration != snapshot.LaunchGeneration ||
            !TryValidateRegularV4ToTunaDrainReceipt(
                state,
                transitionAuthority,
                physicalDrainReceipt,
                out rejectionReason))
        {
            if (string.Equals(rejectionReason, "accepted", StringComparison.Ordinal))
            {
                rejectionReason =
                    "uncommitted_tuna_fallback_route_transition_authority_missing";
            }

            return false;
        }

        if (handoff.HandoffGeneration <= 0 ||
            handoff.SourceDataSessionGeneration <= 0 ||
            handoff.ProofTransportEpoch <= 0 ||
            !peerProof.IsComplete ||
            !peerProof.IsExactFrameBound ||
            peerProof.Direction != FileTransferDirection.Outbound ||
            !string.Equals(peerProof.ProofKind, NormalizeReason(peerProof.ProofKind), StringComparison.Ordinal) ||
            !string.Equals(peerProof.Reason, NormalizeReason(peerProof.Reason), StringComparison.Ordinal) ||
            snapshot.LaunchGeneration <= 0 ||
            !snapshot.PeerVisibleProof ||
            !snapshot.PeerReceived ||
            !snapshot.AnswerReceived ||
            !snapshot.TunaPathLeaseRequired ||
            !snapshot.TunaPathLeaseCurrent ||
            !string.Equals(peerProof.SessionId, state.SessionId, StringComparison.Ordinal) ||
            !string.Equals(peerProof.TransferId, state.TransferId, StringComparison.Ordinal) ||
            peerProof.TransportEpoch != handoff.ProofTransportEpoch ||
            peerProof.TransportEpoch != coordinatorEvent.TransportEpochId ||
            coordinatorEvent.BridgeRecoveryGeneration <= handoff.SourceBridgeRecoveryGeneration ||
            !string.Equals(snapshot.SessionId, state.SessionId, StringComparison.Ordinal) ||
            !string.Equals(snapshot.TransferId, state.TransferId, StringComparison.Ordinal) ||
            admission != pendingPreCommit.AdmissionToken ||
            pendingPreCommit.RuntimeOwner != handoff.SourceRuntimeOwner ||
            snapshot.TransactionGeneration != pendingPreCommit.TransactionGeneration ||
            snapshot.OfferGeneration != pendingPreCommit.OfferGeneration ||
            snapshot.TunaPathLeaseGeneration != pendingPreCommit.TunaPathLeaseGeneration ||
            !string.Equals(
                snapshot.TunaPathLeaseListenerRunId,
                pendingPreCommit.TunaPathLeaseListenerRunId,
                StringComparison.Ordinal) ||
            !string.Equals(snapshot.PathProbeState, "Acked", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(snapshot.PathProbeId) ||
            snapshot.PathProbeTransport != FileTransferTransportKind.Tuna ||
            snapshot.TargetRoute != FileTransferRoute.FileTunaV4 ||
            snapshot.ProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
            snapshot.TargetTransport != FileTransferTransportKind.Tuna ||
            snapshot.HandoffKind != FileTransferTransportHandoffKind.NormalToTunaActivation)
        {
            rejectionReason = "uncommitted_tuna_fallback_precommit_or_drain_mismatch";
            return false;
        }

        return true;
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
        if (state.PersistentState.RegularV4ToTunaRouteTransition.CurrentOperation
            is not null)
        {
            return MutationRejected(
                state,
                reason,
                "regular_v4_to_tuna_transition_invalidation_required");
        }

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
        if (state.PersistentState.RegularV4ToTunaRouteTransition.CurrentOperation
            is not null)
        {
            return MutationRejected(
                state,
                reason,
                "regular_v4_to_tuna_transition_invalidation_required");
        }

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
        if (state.PersistentState.RegularV4ToTunaRouteTransition.CurrentOperation
            is not null)
        {
            return MutationRejected(
                state,
                reason,
                "regular_v4_to_tuna_transition_invalidation_required");
        }

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
        if (state.PersistentState.RegularV4ToTunaRouteTransition.CurrentOperation
            is not null)
        {
            return MutationRejected(
                state,
                reason,
                "regular_v4_to_tuna_transition_invalidation_required");
        }

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
        if (state.PersistentState.RegularV4ToTunaRouteTransition.CurrentOperation
            is not null)
        {
            return MutationRejected(
                state,
                reason,
                "regular_v4_to_tuna_transition_invalidation_required");
        }

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
        FileTransferBridgeRecoveryExecutionCommand? bridgeRecoveryExecutionCommand = null,
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
            bridgeRecoveryExecutionCommand,
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
        RecoveryLivenessLease? recoveryLivenessLease,
        FileTransferBridgeRecoveryExecutionCommand? bridgeRecoveryExecutionCommand)
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

        if (bridgeRecoveryExecutionCommand is { } executionCommand)
        {
            next = next with
            {
                CurrentBridgeRecoveryExecutionCommand = executionCommand,
                CurrentBridgeRecoveryExecutionState =
                    FileTransferBridgeRecoveryExecutionState.Issued,
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
        => state.PersistentState.RegularV4FrontierRepairPressure.HasBinding;

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

        var pressureState = state.PersistentState.RegularV4FrontierRepairPressure;
        var governingBinding =
            pressureState.PendingBinding ??
            pressureState.InstalledBinding;
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

        if (pressureState.PendingBinding is not null)
        {
            rejectionReason =
                "regular_v4_frontier_repair_pressure_binding_repair_replay_pending_conflict";
            return false;
        }

        if (pressureState.RetainedPredecessorBinding is not null)
        {
            rejectionReason =
                "regular_v4_frontier_repair_pressure_binding_predecessor_retirement_pending";
            return false;
        }

        if (pressureState.InstalledBinding is not { } installed ||
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

    private static bool TryValidateRegularV4ToTunaPrepareRequest(
        FileTransferCoordinatorState state,
        RegularV4ToTunaRouteTransitionPrepareRequest request,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        if (state.IsTerminal)
        {
            rejectionReason = "transfer_terminal";
            return false;
        }

        if (state.RouteSelection.Route != FileTransferRoute.RegularNknV4Fast ||
            state.RouteSelection.ProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
            state.CurrentRouteCommit is not null ||
            state.CurrentRuntimeOwner is not { } owner ||
            state.CurrentLeg is not { } leg)
        {
            rejectionReason = "regular_v4_to_tuna_transition_source_route_invalid";
            return false;
        }

        if (request.SourceRuntimeOwner != owner ||
            !IsRuntimeOwnerCurrent(state, request.SourceRuntimeOwner, out rejectionReason) ||
            state.CurrentRoutePublicationToken != request.SourceRoutePublication ||
            !IsRoutePublicationTokenCurrent(
                state,
                request.SourceRoutePublication,
                out rejectionReason) ||
            state.CurrentRuntimeUnlockAdmission != request.RuntimeUnlockAdmission ||
            !IsRuntimeUnlockAdmissionTokenCurrent(
                state,
                request.RuntimeUnlockAdmission,
                out rejectionReason) ||
            state.CurrentRuntimeUnlockPendingPreCommitProbe !=
                request.PendingPreCommitProbe ||
            !IsRuntimeUnlockPendingPreCommitProbeRequestCurrent(
                state,
                request.PendingPreCommitProbe,
                out rejectionReason))
        {
            if (string.Equals(rejectionReason, "accepted", StringComparison.Ordinal))
            {
                rejectionReason = "regular_v4_to_tuna_transition_source_authority_mismatch";
            }

            return false;
        }

        if (request.LaunchGeneration <= 0 ||
            !string.Equals(
                request.AcceptedLaunchBoundary.SessionId,
                state.SessionId,
                StringComparison.Ordinal) ||
            (!string.IsNullOrWhiteSpace(request.AcceptedLaunchBoundary.TransferId) &&
             !string.Equals(
                 request.AcceptedLaunchBoundary.TransferId,
                 state.TransferId,
                 StringComparison.Ordinal)) ||
            request.PendingPreCommitProbe.RuntimeOwner != owner ||
            request.PendingPreCommitProbe.AdmissionToken != request.RuntimeUnlockAdmission ||
            request.PendingPreCommitProbe.TransactionGeneration !=
                request.AcceptedLaunchBoundary.TransactionGeneration ||
            request.PendingPreCommitProbe.OfferGeneration !=
                request.AcceptedLaunchBoundary.OfferGeneration ||
            request.PendingPreCommitProbe.TunaPathLeaseGeneration !=
                request.AcceptedLaunchBoundary.TunaPathLeaseGeneration ||
            !string.Equals(
                request.PendingPreCommitProbe.TunaPathLeaseListenerRunId,
                request.AcceptedLaunchBoundary.TunaPathLeaseListenerRunId,
                StringComparison.Ordinal) ||
            !RegularV4ToTunaProofMatchesTarget(request.AcceptedLaunchBoundary) ||
            request.AcceptedLaunchBoundary.AdmissionToken != request.RuntimeUnlockAdmission)
        {
            rejectionReason = "regular_v4_to_tuna_transition_launch_boundary_mismatch";
            return false;
        }

        var checkpoint = request.DurableCheckpoint;
        if (checkpoint.IsDefault ||
            checkpoint.SourceRuntimeOwner != owner ||
            checkpoint.SourceRoutePublication != request.SourceRoutePublication ||
            checkpoint.SourceTerminalGeneration != state.TerminalGeneration ||
            checkpoint.SourceLegGeneration != leg.Generation ||
            checkpoint.SourceTransportEpoch != leg.TransportEpochId ||
            checkpoint.SourceBridgeRecoveryGeneration != leg.BridgeRecoveryGeneration)
        {
            rejectionReason = "regular_v4_to_tuna_transition_checkpoint_scope_mismatch";
            return false;
        }

        return true;
    }

    private static bool IsRegularV4ToTunaRouteTransitionSourceScopeCurrent(
        FileTransferCoordinatorState state,
        RegularV4ToTunaRouteTransitionAuthority authority,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        if (authority.IsDefault ||
            !string.Equals(state.SessionId, authority.SessionId, StringComparison.Ordinal) ||
            !string.Equals(state.TransferId, authority.TransferId, StringComparison.Ordinal) ||
            state.Direction != authority.Direction)
        {
            rejectionReason = "regular_v4_to_tuna_transition_identity_mismatch";
            return false;
        }

        if (state.IsTerminal)
        {
            rejectionReason = "transfer_terminal";
            return false;
        }

        if (state.RouteSelection.Route != FileTransferRoute.RegularNknV4Fast ||
            state.RouteSelection.ProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
            state.CurrentRouteCommit is not null ||
            state.CurrentRuntimeOwner != authority.SourceRuntimeOwner ||
            state.CurrentRoutePublicationToken != authority.SourceRoutePublication ||
            state.CurrentRuntimeUnlockAdmission != authority.RuntimeUnlockAdmission ||
            state.CurrentRuntimeUnlockPendingPreCommitProbe != authority.PendingPreCommitProbe ||
            state.TerminalGeneration != authority.SourceTerminalGeneration ||
            (state.CurrentLiveRouteEpoch?.EpochId ?? 0) != authority.SourceLiveRouteEpoch ||
            state.CurrentLeg is not { } leg ||
            leg.Generation != authority.SourceLegGeneration ||
            leg.TransportEpochId != authority.SourceTransportEpoch ||
            leg.BridgeRecoveryGeneration != authority.SourceBridgeRecoveryGeneration)
        {
            rejectionReason = "regular_v4_to_tuna_transition_source_scope_stale";
            return false;
        }

        if (!IsRuntimeOwnerCurrent(state, authority.SourceRuntimeOwner, out rejectionReason) ||
            !IsRoutePublicationTokenCurrent(
                state,
                authority.SourceRoutePublication,
                out rejectionReason) ||
            !IsRuntimeUnlockAdmissionTokenCurrent(
                state,
                authority.RuntimeUnlockAdmission,
                out rejectionReason) ||
            !IsRuntimeUnlockPendingPreCommitProbeRequestCurrent(
                state,
                authority.PendingPreCommitProbe,
                out rejectionReason))
        {
            return false;
        }

        return authority.DurableCheckpoint.SourceRuntimeOwner ==
                   authority.SourceRuntimeOwner &&
               authority.DurableCheckpoint.SourceRoutePublication ==
                   authority.SourceRoutePublication &&
               authority.DurableCheckpoint.SourceTerminalGeneration ==
                   authority.SourceTerminalGeneration &&
               authority.DurableCheckpoint.SourceLegGeneration ==
                   authority.SourceLegGeneration &&
               authority.DurableCheckpoint.SourceTransportEpoch ==
                   authority.SourceTransportEpoch &&
               authority.DurableCheckpoint.SourceBridgeRecoveryGeneration ==
                   authority.SourceBridgeRecoveryGeneration &&
               authority.DurableCheckpoint.DataSessionGeneration ==
                   authority.DataSessionGeneration;
    }

    private static bool TryValidateRegularV4ToTunaFinalProof(
        FileTransferCoordinatorState state,
        RegularV4ToTunaRouteTransitionAuthority authority,
        RuntimeUnlockRouteCommitProof proof,
        out string rejectionReason)
    {
        if (!RuntimeUnlockTransaction.CanCommitRoute(proof, state, out rejectionReason))
        {
            return false;
        }

        var prepared = authority.AcceptedLaunchBoundary;
        if (!RegularV4ToTunaProofMatchesTarget(proof) ||
            proof.AdmissionToken != authority.RuntimeUnlockAdmission ||
            proof.TransactionGeneration != prepared.TransactionGeneration ||
            proof.OfferGeneration != prepared.OfferGeneration ||
            proof.TunaPathLeaseGeneration != prepared.TunaPathLeaseGeneration ||
            !string.Equals(
                proof.TunaPathLeaseListenerRunId,
                prepared.TunaPathLeaseListenerRunId,
                StringComparison.Ordinal) ||
            authority.PendingPreCommitProbe.TransactionGeneration !=
                proof.TransactionGeneration ||
            authority.PendingPreCommitProbe.OfferGeneration != proof.OfferGeneration ||
            authority.PendingPreCommitProbe.TunaPathLeaseGeneration !=
                proof.TunaPathLeaseGeneration ||
            !string.Equals(
                authority.PendingPreCommitProbe.TunaPathLeaseListenerRunId,
                proof.TunaPathLeaseListenerRunId,
                StringComparison.Ordinal))
        {
            rejectionReason = "regular_v4_to_tuna_transition_final_proof_mismatch";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(prepared.PathProbeId) &&
            !string.Equals(prepared.PathProbeId, proof.PathProbeId, StringComparison.Ordinal))
        {
            rejectionReason = "regular_v4_to_tuna_transition_probe_identity_mismatch";
            return false;
        }

        rejectionReason = "accepted";
        return true;
    }

    private static bool RegularV4ToTunaProofMatchesTarget(
        RuntimeUnlockRouteCommitProof proof)
        => proof.TargetRoute == FileTransferRoute.FileTunaV4 &&
           proof.ProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
           proof.TargetTransport == FileTransferTransportKind.Tuna &&
           proof.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
           proof.TransactionGeneration > 0 &&
           proof.OfferGeneration > 0 &&
           proof.PeerVisibleProof &&
           (proof.PeerReceived || proof.AnswerReceived) &&
           proof.TransactionState is not (
               RuntimeUnlockTransactionState.Failed or
               RuntimeUnlockTransactionState.Retired or
               RuntimeUnlockTransactionState.Committed) &&
           proof.PathProbeState != RuntimeUnlockPathProbeState.Failed &&
           proof.TunaPathLeaseGeneration > 0 &&
           proof.TunaPathLeaseState == RuntimeUnlockTunaPathLeaseState.ListenerReady &&
           proof.TunaPathLeaseCurrent;

    private static bool RegularV4ToTunaPrepareRequestMatchesAuthority(
        RegularV4ToTunaRouteTransitionPrepareRequest request,
        RegularV4ToTunaRouteTransitionAuthority authority)
        => request.SourceRuntimeOwner == authority.SourceRuntimeOwner &&
           request.SourceRoutePublication == authority.SourceRoutePublication &&
           request.RuntimeUnlockAdmission == authority.RuntimeUnlockAdmission &&
           request.PendingPreCommitProbe == authority.PendingPreCommitProbe &&
           request.AcceptedLaunchBoundary == authority.AcceptedLaunchBoundary &&
           request.LaunchGeneration == authority.LaunchGeneration &&
           request.DurableCheckpoint == authority.DurableCheckpoint &&
           string.Equals(request.Reason, authority.Reason, StringComparison.Ordinal);

    private static bool TryValidateCapturedPhysicalSendLeaseSet(
        long capturedThroughPhysicalSendGeneration,
        IReadOnlyList<long> capturedPhysicalSendLeaseIds,
        string capturedPhysicalSendFingerprint,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        if (capturedThroughPhysicalSendGeneration < 0 ||
            capturedPhysicalSendLeaseIds is null ||
            string.IsNullOrWhiteSpace(capturedPhysicalSendFingerprint) ||
            !string.Equals(
                capturedPhysicalSendFingerprint,
                CreateRegularV4ToTunaPhysicalDrainFingerprint(
                    capturedPhysicalSendLeaseIds),
                StringComparison.Ordinal))
        {
            rejectionReason = "regular_v4_to_tuna_transition_drain_boundary_invalid";
            return false;
        }

        long previous = 0;
        foreach (var leaseId in capturedPhysicalSendLeaseIds)
        {
            if (leaseId <= previous ||
                leaseId > capturedThroughPhysicalSendGeneration)
            {
                rejectionReason = "regular_v4_to_tuna_transition_drain_lease_set_invalid";
                return false;
            }

            previous = leaseId;
        }

        return true;
    }

    private static bool TryValidateRegularV4ToTunaDrainReceipt(
        FileTransferCoordinatorState state,
        RegularV4ToTunaRouteTransitionAuthority authority,
        RegularV4ToTunaPhysicalDrainReceipt receipt,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        var operation =
            state.PersistentState.RegularV4ToTunaRouteTransition.CurrentOperation;
        if (operation is not
            {
                State: RegularV4ToTunaRouteTransitionState.Draining,
                DrainCommand: { } command,
            } ||
            operation.Value.Authority != authority ||
            !IsRegularV4ToTunaRouteTransitionSourceScopeCurrent(
                state,
                authority,
                out rejectionReason))
        {
            if (string.Equals(rejectionReason, "accepted", StringComparison.Ordinal))
            {
                rejectionReason = "regular_v4_to_tuna_transition_not_draining";
            }

            return false;
        }

        if (receipt.Command != command ||
            receipt.Outcome != RegularV4ToTunaPhysicalDrainOutcome.Completed ||
            receipt.CompletedPhysicalSendCount != command.CapturedPhysicalSendCount ||
            receipt.CompletedPhysicalSendLeaseIds is null ||
            !CapturedLeaseSetsEqual(
                receipt.CompletedPhysicalSendLeaseIds,
                command.CapturedPhysicalSendLeaseIds) ||
            !string.Equals(
                receipt.CompletedPhysicalSendFingerprint,
                command.CapturedPhysicalSendFingerprint,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(receipt.Reason) ||
            !string.Equals(
                receipt.Reason,
                NormalizeReason(receipt.Reason),
                StringComparison.Ordinal))
        {
            rejectionReason = "regular_v4_to_tuna_transition_drain_receipt_mismatch";
            return false;
        }

        return true;
    }

    private static bool IsExactOrCanonicalIdempotentRegularV4ToTunaDrainReceipt(
        RegularV4ToTunaPhysicalDrainReceipt committed,
        RegularV4ToTunaPhysicalDrainReceipt candidate)
        => committed.Command == candidate.Command &&
           candidate.Outcome == RegularV4ToTunaPhysicalDrainOutcome.Completed &&
           committed.CompletedPhysicalSendCount == candidate.CompletedPhysicalSendCount &&
           CapturedLeaseSetsEqual(
               committed.CompletedPhysicalSendLeaseIds,
               candidate.CompletedPhysicalSendLeaseIds) &&
           string.Equals(
               committed.CompletedPhysicalSendFingerprint,
               candidate.CompletedPhysicalSendFingerprint,
               StringComparison.Ordinal) &&
           string.Equals(
               committed.Reason,
               candidate.Reason,
               StringComparison.Ordinal);

    private static RegularV4ToTunaPhysicalDrainReceipt
        SnapshotRegularV4ToTunaDrainReceipt(
            RegularV4ToTunaPhysicalDrainReceipt receipt)
        => receipt with
        {
            CompletedPhysicalSendLeaseIds = Array.AsReadOnly(
                receipt.CompletedPhysicalSendLeaseIds.ToArray()),
            Reason = NormalizeReason(receipt.Reason),
        };

    private static bool IsExactRegularV4ToTunaPressureReceiptDuplicate(
        FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt? committed,
        FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt? candidate)
        => committed is null
            ? candidate is null
            : candidate is { } receipt &&
              (receipt == committed ||
               (receipt.Command == committed.Command &&
                receipt.Status ==
                    FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Idempotent &&
                receipt.CurrentAuthorityCleared ==
                    committed.CurrentAuthorityCleared &&
                receipt.RetainedPredecessorCleared ==
                    committed.RetainedPredecessorCleared &&
                string.Equals(
                    receipt.Reason,
                    "regular_v4_frontier_repair_pressure_settlement_apply_idempotent",
                    StringComparison.Ordinal)));

    private static bool IsCanonicalRegularV4ToTunaPressureSettlementReceipt(
        FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt receipt)
        => receipt.Status switch
        {
            FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Applied =>
                string.Equals(
                    receipt.Reason,
                    "regular_v4_frontier_repair_pressure_settlement_applied",
                    StringComparison.Ordinal),
            FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Idempotent =>
                string.Equals(
                    receipt.Reason,
                    "regular_v4_frontier_repair_pressure_settlement_apply_idempotent",
                    StringComparison.Ordinal),
            _ => false,
        };

    private static bool IsExactOrCanonicalIdempotentRegularV4ToTunaPauseBindReceipt(
        RegularV4ToTunaActivationPauseBindReceipt committed,
        RegularV4ToTunaActivationPauseBindReceipt candidate)
        => committed == candidate ||
           (committed.Command == candidate.Command &&
            candidate.Status == RegularV4ToTunaActivationPauseBindStatus.Idempotent &&
            string.Equals(
                candidate.Reason,
                "regular_v4_to_tuna_activation_pause_bind_idempotent",
                StringComparison.Ordinal));

    private static bool IsCanonicalRegularV4ToTunaPauseBindReceipt(
        RegularV4ToTunaActivationPauseBindReceipt receipt)
        => receipt.Status switch
        {
            RegularV4ToTunaActivationPauseBindStatus.Installed =>
                string.Equals(
                    receipt.Reason,
                    "regular_v4_to_tuna_activation_pause_bound",
                    StringComparison.Ordinal),
            RegularV4ToTunaActivationPauseBindStatus.Idempotent =>
                string.Equals(
                    receipt.Reason,
                    "regular_v4_to_tuna_activation_pause_bind_idempotent",
                    StringComparison.Ordinal),
            _ => false,
        };

    private static bool IsCanonicalAppliedRegularV4ToTunaDispositionReceipt(
        RegularV4ToTunaRouteTransitionDispositionReceipt receipt)
        => receipt.Status ==
               RegularV4ToTunaRouteTransitionDispositionApplyStatus.Applied &&
           string.Equals(
               receipt.Reason,
               receipt.Command.Kind switch
               {
                   RegularV4ToTunaRouteTransitionDispositionKind.Committed =>
                       "regular_v4_to_tuna_route_transition_committed",
                   RegularV4ToTunaRouteTransitionDispositionKind.RolledBack =>
                       "regular_v4_to_tuna_route_transition_rolled_back",
                   RegularV4ToTunaRouteTransitionDispositionKind.Superseded =>
                       "regular_v4_to_tuna_route_transition_superseded",
                   _ => "regular_v4_to_tuna_route_transition_terminal_invalidated",
               },
               StringComparison.Ordinal);

    private static bool IsCanonicalAcceptedRegularV4ToTunaDispositionReceipt(
        RegularV4ToTunaRouteTransitionDispositionReceipt receipt)
        => IsCanonicalAppliedRegularV4ToTunaDispositionReceipt(receipt) ||
           (receipt.Status ==
                RegularV4ToTunaRouteTransitionDispositionApplyStatus.Idempotent &&
            string.Equals(
                receipt.Reason,
                "regular_v4_to_tuna_route_transition_disposition_idempotent",
                StringComparison.Ordinal));

    private static bool IsCanonicalIdempotentRegularV4ToTunaDispositionReceipt(
        RegularV4ToTunaRouteTransitionDispositionReceipt applied,
        RegularV4ToTunaRouteTransitionDispositionReceipt candidate)
        => candidate.Command == applied.Command &&
           candidate.Status ==
               RegularV4ToTunaRouteTransitionDispositionApplyStatus.Idempotent &&
           string.Equals(
               candidate.Reason,
               "regular_v4_to_tuna_route_transition_disposition_idempotent",
               StringComparison.Ordinal);

    private static bool CapturedLeaseSetsEqual(
        IReadOnlyList<long> left,
        IReadOnlyList<long> right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (left[index] != right[index])
            {
                return false;
            }
        }

        return true;
    }

    private static FileTransferRegularV4FrontierRepairSettlement
        CreateRegularV4ToTunaTransitionPressureSettlement(
            FileTransferRegularV4FrontierRepairAuthority authority)
        => new(
            authority.SessionId,
            authority.TransferId,
            authority.Direction,
            authority.RouteToken,
            authority.ProtocolVersion,
            authority.LiveRouteEpoch,
            authority.RuntimeGeneration,
            authority.TerminalGeneration,
            authority.LegGeneration,
            authority.TransportEpoch,
            authority.BridgeRecoveryGeneration,
            authority.BridgeDecisionGeneration,
            authority.RoutePublicationGeneration,
            authority.RouteCommitGeneration,
            authority.RepairKey,
            authority.FrontierChunkIndex,
            authority.DurableReceivedHighestChunkIndex,
            authority.PendingRepairCount,
            authority.DecisionGeneration,
            "regular_v4_to_tuna_route_transition_pressure_handoff",
            FileTransferRegularV4FrontierRepairSettlementOutcome.StaleRejected,
            authority.TargetTransport)
        {
            AuthorityId = authority.AuthorityId,
        };

    private static RegularV4ToTunaRouteTransitionDecision RegularV4ToTunaDecision(
        FileTransferCoordinatorState state,
        RegularV4ToTunaRouteTransitionDecisionStatus status,
        string reason,
        RegularV4ToTunaRouteTransitionAuthority? authority = null,
        RegularV4ToTunaActivationPauseBindCommand? pauseBindCommand = null,
        RegularV4ToTunaPhysicalDrainCommand? drainCommand = null,
        RegularV4ToTunaRouteTransitionDispositionCommand? dispositionCommand = null,
        FileTransferCoordinatorDecision? routeDecision = null)
        => new(
            status,
            NormalizeReason(reason),
            authority,
            pauseBindCommand,
            drainCommand,
            dispositionCommand,
            routeDecision)
        {
            State = state,
        };

    private static bool TryConvertRegularV4ToTunaPrepareRequest(
        FileTransferRouteTransitionPrepareRequest source,
        out RegularV4ToTunaRouteTransitionPrepareRequest request,
        out string rejectionReason)
    {
        request = default;
        rejectionReason = "accepted";
        if (source.Kind != FileTransferRouteTransitionKind.RegularV4ToTunaV4 ||
            source.SourceRoute != FileTransferRoute.RegularNknV4Fast ||
            source.SourceProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
            source.SourceTransport != FileTransferTransportKind.RegularNkn ||
            source.SourceRouteCommit is not null ||
            source.TargetRoute != FileTransferRoute.FileTunaV4 ||
            source.TargetProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
            source.TargetTransport != FileTransferTransportKind.Tuna ||
            source.HandoffKind !=
                FileTransferTransportHandoffKind.NormalToTunaActivation ||
            source.PredecessorTransitionAuthorityId is not null ||
            source.ProofBoundary is not
                FileTransferTunaActivationTransitionProofBoundary proof ||
            proof.EffectiveRetryInstallReceipt is not null ||
            proof.FallbackTailYieldDecision is not null)
        {
            rejectionReason = "filetransfer_route_transition_regular_v4_boundary_invalid";
            return false;
        }

        request = new RegularV4ToTunaRouteTransitionPrepareRequest(
            source.SourceRuntimeOwner,
            source.SourceRoutePublication,
            proof.RuntimeUnlockAdmission,
            proof.PendingPreCommitProbe,
            proof.AcceptedLaunchBoundary,
            proof.LaunchGeneration,
            FileTransferRouteTransitionCompatibility.ToRegularV4ToTuna(
                source.DurableCheckpoint),
            source.Reason);
        return true;
    }

    private static FileTransferRouteTransitionDecision FromRegularV4ToTunaDecision(
        RegularV4ToTunaRouteTransitionDecision source)
        => new(
            (FileTransferRouteTransitionDecisionStatus)(int)source.Status,
            source.Reason,
            source.Authority is { } authority
                ? FileTransferRouteTransitionCompatibility.FromRegularV4ToTuna(authority)
                : null,
            source.PauseBindCommand is { } pause
                ? new FileTransferRouteTransitionPauseBindCommand(
                    FileTransferRouteTransitionCompatibility.FromRegularV4ToTuna(
                        pause.Authority))
                : null,
            source.DrainCommand is { } drain
                ? new FileTransferRouteTransitionPhysicalDrainCommand(
                    FileTransferRouteTransitionCompatibility.FromRegularV4ToTuna(
                        drain.Authority),
                    drain.CapturedThroughPhysicalSendGeneration,
                    drain.CapturedPhysicalSendLeaseIds,
                    drain.CapturedPhysicalSendCount,
                    drain.CapturedPhysicalSendFingerprint)
                : null,
            source.DispositionCommand is { } disposition
                ? new FileTransferRouteTransitionDispositionCommand(
                    FileTransferRouteTransitionCompatibility.FromRegularV4ToTuna(
                        disposition.Authority),
                    (FileTransferRouteTransitionDispositionKind)(int)disposition.Kind,
                    disposition.RouteCommitToken,
                    disposition.RoutePublicationToken,
                    disposition.Reason)
                {
                    FinalProof = disposition.FinalProof,
                }
                : null,
            source.RouteDecision)
        {
            State = source.State,
        };

    private static bool TryValidateFileTransferRouteTransitionPrepareRequest(
        FileTransferCoordinatorState state,
        FileTransferRouteTransitionPrepareRequest request,
        out string rejectionReason,
        bool allowCurrentOperation = false)
    {
        rejectionReason = "accepted";
        if (state.IsTerminal)
        {
            rejectionReason = "transfer_terminal";
            return false;
        }

        if (!IsFileTransferRouteTransitionKindValid(request))
        {
            rejectionReason = "filetransfer_route_transition_kind_invalid";
            return false;
        }

        if (!allowCurrentOperation &&
            state.PersistentState.FileTransferRouteTransition.CurrentOperation is not null)
        {
            rejectionReason = "filetransfer_route_transition_conflict";
            return false;
        }

        if (state.CurrentRuntimeOwner is not { } owner ||
            state.CurrentRoutePublicationToken is not { } publication ||
            state.CurrentLeg is not { } leg ||
            request.SourceRuntimeOwner != owner ||
            request.SourceRoutePublication != publication ||
            request.SourceRoute != state.RouteSelection.Route ||
            request.SourceProtocolVersion != state.RouteSelection.ProtocolVersion ||
            request.SourceTransport !=
                ExpectedRoutePublicationTransport(state.RouteSelection.Route) ||
            !IsRuntimeOwnerCurrent(state, owner, out rejectionReason) ||
            !IsRoutePublicationTokenCurrent(state, publication, out rejectionReason))
        {
            if (string.Equals(rejectionReason, "accepted", StringComparison.Ordinal))
            {
                rejectionReason = "filetransfer_route_transition_source_authority_mismatch";
            }

            return false;
        }

        if (request.SourceRouteCommit is { } sourceCommit)
        {
            if (state.CurrentRouteCommit != sourceCommit ||
                !IsRouteCommitTokenCurrent(state, sourceCommit, out rejectionReason))
            {
                if (string.Equals(rejectionReason, "accepted", StringComparison.Ordinal))
                {
                    rejectionReason = "filetransfer_route_transition_source_commit_mismatch";
                }

                return false;
            }
        }
        else if (request.Kind ==
                     FileTransferRouteTransitionKind.TunaV4ToPostTunaFallbackV6 &&
                 state.CurrentRouteCommit is not null)
        {
            rejectionReason = "filetransfer_route_transition_source_commit_missing";
            return false;
        }

        if (request.ProofBoundary is null ||
            request.ProofBoundary.IsDefault ||
            request.DurableCheckpoint.IsDefault ||
            request.DurableCheckpoint.SourceRuntimeOwner != owner ||
            request.DurableCheckpoint.SourceRoutePublication != publication ||
            request.DurableCheckpoint.SourceTerminalGeneration != state.TerminalGeneration ||
            request.DurableCheckpoint.SourceLegGeneration != leg.Generation ||
            request.DurableCheckpoint.SourceTransportEpoch != leg.TransportEpochId ||
            request.DurableCheckpoint.SourceBridgeRecoveryGeneration !=
                leg.BridgeRecoveryGeneration)
        {
            rejectionReason = "filetransfer_route_transition_checkpoint_or_proof_mismatch";
            return false;
        }

        if (request.Kind ==
            FileTransferRouteTransitionKind.PostTunaFallbackV6ToTunaV4)
        {
            if (request.ProofBoundary is not
                FileTransferTunaActivationTransitionProofBoundary
                {
                    EffectiveRetryInstallReceipt: { IsDefault: false } receipt,
                    FallbackTailYieldDecision:
                    {
                        IsDefault: false,
                        Accepted: true,
                        YieldKind: FallbackTailYieldKind.Quiescent,
                    } yield,
                } ||
                receipt.Authority.ReleaseKind !=
                    RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield ||
                receipt.Authority.ReleaseAuthorityId != yield.AuthorityId ||
                receipt.Authority.ReleaseGeneration != yield.YieldGeneration ||
                !string.Equals(
                    receipt.Authority.ReleaseOutcome,
                    FallbackTailYieldKind.Quiescent.ToString(),
                    StringComparison.Ordinal))
            {
                rejectionReason =
                    "filetransfer_route_transition_fallback_release_boundary_invalid";
                return false;
            }
        }

        if (request.Kind is
                FileTransferRouteTransitionKind.TunaV4ToPostTunaFallbackV6 or
                FileTransferRouteTransitionKind.UncommittedTunaToPostTunaFallbackV6 &&
            request.ProofBoundary is not FileTransferFallbackTransitionProofBoundary)
        {
            rejectionReason = "filetransfer_route_transition_peer_proof_missing";
            return false;
        }

        return true;
    }

    private static bool IsFileTransferRouteTransitionKindValid(
        FileTransferRouteTransitionPrepareRequest request)
        => request.Kind switch
        {
            FileTransferRouteTransitionKind.RegularV4ToTunaV4 =>
                request.SourceRoute == FileTransferRoute.RegularNknV4Fast &&
                request.SourceProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
                request.SourceTransport == FileTransferTransportKind.RegularNkn &&
                request.TargetRoute == FileTransferRoute.FileTunaV4 &&
                request.TargetProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
                request.TargetTransport == FileTransferTransportKind.Tuna &&
                request.HandoffKind ==
                    FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferRouteTransitionKind.TunaV4ToPostTunaFallbackV6 =>
                request.SourceRoute == FileTransferRoute.FileTunaV4 &&
                request.SourceProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
                request.SourceTransport == FileTransferTransportKind.Tuna &&
                request.TargetRoute == FileTransferRoute.PostTunaFallbackV6 &&
                request.TargetProtocolVersion == FileTransferProtocol.ProtocolVersionV6 &&
                request.TargetTransport == FileTransferTransportKind.RegularNkn &&
                request.HandoffKind ==
                    FileTransferTransportHandoffKind.TunaToNormalFallback &&
                request.PredecessorTransitionAuthorityId is null,
            FileTransferRouteTransitionKind.PostTunaFallbackV6ToTunaV4 =>
                request.SourceRoute == FileTransferRoute.PostTunaFallbackV6 &&
                request.SourceProtocolVersion == FileTransferProtocol.ProtocolVersionV6 &&
                request.SourceTransport == FileTransferTransportKind.RegularNkn &&
                request.TargetRoute == FileTransferRoute.FileTunaV4 &&
                request.TargetProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
                request.TargetTransport == FileTransferTransportKind.Tuna &&
                request.HandoffKind ==
                    FileTransferTransportHandoffKind.NormalToTunaActivation &&
                request.PredecessorTransitionAuthorityId is null,
            FileTransferRouteTransitionKind.UncommittedTunaToPostTunaFallbackV6 =>
                request.SourceRoute == FileTransferRoute.RegularNknV4Fast &&
                request.SourceProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
                request.SourceTransport == FileTransferTransportKind.RegularNkn &&
                request.SourceRouteCommit is null &&
                request.TargetRoute == FileTransferRoute.PostTunaFallbackV6 &&
                request.TargetProtocolVersion == FileTransferProtocol.ProtocolVersionV6 &&
                request.TargetTransport == FileTransferTransportKind.RegularNkn &&
                request.HandoffKind ==
                    FileTransferTransportHandoffKind.TunaToNormalFallback &&
                request.PredecessorTransitionAuthorityId is { IsDefault: false },
            _ => false,
        };

    private static FileTransferRouteTransitionAuthority
        CreateFileTransferRouteTransitionAuthority(
            FileTransferCoordinatorState state,
            FileTransferRouteTransitionPrepareRequest request,
            long transitionGeneration)
    {
        var owner = state.CurrentRuntimeOwner!.Value;
        var leg = state.CurrentLeg!;
        return new FileTransferRouteTransitionAuthority(
            request.Kind,
            state.SessionId,
            state.TransferId,
            state.Direction,
            owner,
            request.SourceRoutePublication,
            request.SourceRouteCommit,
            request.SourceRoute,
            request.SourceProtocolVersion,
            request.SourceTransport,
            state.TerminalGeneration,
            state.CurrentLiveRouteEpoch?.EpochId ?? 0,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            request.DurableCheckpoint.DataSessionGeneration,
            request.ProofBoundary,
            request.DurableCheckpoint,
            request.TargetRoute,
            request.TargetProtocolVersion,
            request.TargetTransport,
            request.HandoffKind,
            transitionGeneration,
            IssueAuthorityId(
                "filetransfer_route_transition",
                state,
                checked((int)Math.Min(int.MaxValue, transitionGeneration))),
            request.PredecessorTransitionAuthorityId,
            NormalizeReason(request.Reason));
    }

    private static bool IsFileTransferRouteTransitionSourceScopeCurrent(
        FileTransferCoordinatorState state,
        FileTransferRouteTransitionAuthority authority,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        if (authority.IsDefault ||
            !string.Equals(state.SessionId, authority.SessionId, StringComparison.Ordinal) ||
            !string.Equals(state.TransferId, authority.TransferId, StringComparison.Ordinal) ||
            state.Direction != authority.Direction)
        {
            rejectionReason = "filetransfer_route_transition_identity_mismatch";
            return false;
        }

        if (state.IsTerminal)
        {
            rejectionReason = "transfer_terminal";
            return false;
        }

        if (state.RouteSelection.Route != authority.SourceRoute ||
            state.RouteSelection.ProtocolVersion != authority.SourceProtocolVersion ||
            ExpectedRoutePublicationTransport(state.RouteSelection.Route) !=
                authority.SourceTransport ||
            state.CurrentRuntimeOwner != authority.SourceRuntimeOwner ||
            state.CurrentRoutePublicationToken != authority.SourceRoutePublication ||
            state.TerminalGeneration != authority.SourceTerminalGeneration ||
            (state.CurrentLiveRouteEpoch?.EpochId ?? 0) != authority.SourceLiveRouteEpoch ||
            state.CurrentLeg is not { } leg ||
            leg.Generation != authority.SourceLegGeneration ||
            leg.TransportEpochId != authority.SourceTransportEpoch ||
            leg.BridgeRecoveryGeneration != authority.SourceBridgeRecoveryGeneration)
        {
            rejectionReason = "filetransfer_route_transition_source_scope_stale";
            return false;
        }

        if (authority.SourceRouteCommit is { } sourceCommit &&
            state.CurrentRouteCommit != sourceCommit)
        {
            rejectionReason = "filetransfer_route_transition_source_commit_stale";
            return false;
        }

        if (!IsRuntimeOwnerCurrent(state, authority.SourceRuntimeOwner, out rejectionReason) ||
            !IsRoutePublicationTokenCurrent(
                state,
                authority.SourceRoutePublication,
                out rejectionReason))
        {
            return false;
        }

        return authority.DurableCheckpoint.SourceRuntimeOwner ==
                   authority.SourceRuntimeOwner &&
               authority.DurableCheckpoint.SourceRoutePublication ==
                   authority.SourceRoutePublication &&
               authority.DurableCheckpoint.SourceTerminalGeneration ==
                   authority.SourceTerminalGeneration &&
               authority.DurableCheckpoint.SourceLegGeneration ==
                   authority.SourceLegGeneration &&
               authority.DurableCheckpoint.SourceTransportEpoch ==
                   authority.SourceTransportEpoch &&
               authority.DurableCheckpoint.SourceBridgeRecoveryGeneration ==
                   authority.SourceBridgeRecoveryGeneration &&
               authority.DurableCheckpoint.DataSessionGeneration ==
                   authority.DataSessionGeneration;
    }

    private static bool FileTransferRouteTransitionPrepareRequestMatchesAuthority(
        FileTransferRouteTransitionPrepareRequest request,
        FileTransferRouteTransitionAuthority authority)
        => request.Kind == authority.Kind &&
           request.SourceRuntimeOwner == authority.SourceRuntimeOwner &&
           request.SourceRoutePublication == authority.SourceRoutePublication &&
           request.SourceRouteCommit == authority.SourceRouteCommit &&
           request.SourceRoute == authority.SourceRoute &&
           request.SourceProtocolVersion == authority.SourceProtocolVersion &&
           request.SourceTransport == authority.SourceTransport &&
           request.ProofBoundary == authority.ProofBoundary &&
           request.DurableCheckpoint == authority.DurableCheckpoint &&
           request.TargetRoute == authority.TargetRoute &&
           request.TargetProtocolVersion == authority.TargetProtocolVersion &&
           request.TargetTransport == authority.TargetTransport &&
           request.HandoffKind == authority.HandoffKind &&
           request.PredecessorTransitionAuthorityId ==
               authority.PredecessorTransitionAuthorityId &&
           string.Equals(request.Reason, authority.Reason, StringComparison.Ordinal);

    private static bool IsExactOrCanonicalIdempotentFileTransferRouteTransitionPauseBindReceipt(
        FileTransferRouteTransitionPauseBindReceipt committed,
        FileTransferRouteTransitionPauseBindReceipt candidate)
        => committed == candidate ||
           (committed.Command == candidate.Command &&
            candidate.Status == FileTransferRouteTransitionPauseBindStatus.Idempotent &&
            string.Equals(
                candidate.Reason,
                "filetransfer_route_transition_pause_bind_idempotent",
                StringComparison.Ordinal));

    private static bool IsCanonicalFileTransferRouteTransitionPauseBindReceipt(
        FileTransferRouteTransitionPauseBindReceipt receipt)
        => receipt.Status switch
        {
            FileTransferRouteTransitionPauseBindStatus.Installed =>
                string.Equals(
                    receipt.Reason,
                    "filetransfer_route_transition_pause_bound",
                    StringComparison.Ordinal),
            FileTransferRouteTransitionPauseBindStatus.Idempotent =>
                string.Equals(
                    receipt.Reason,
                    "filetransfer_route_transition_pause_bind_idempotent",
                    StringComparison.Ordinal),
            _ => false,
        };

    private static FileTransferRouteTransitionDecision
        FileTransferRouteTransitionDecisionFor(
            FileTransferCoordinatorState state,
            FileTransferRouteTransitionDecisionStatus status,
            string reason,
            FileTransferRouteTransitionAuthority? authority = null,
            FileTransferRouteTransitionPauseBindCommand? pauseBindCommand = null,
            FileTransferRouteTransitionPhysicalDrainCommand? drainCommand = null,
            FileTransferRouteTransitionDispositionCommand? dispositionCommand = null,
            FileTransferCoordinatorDecision? routeDecision = null)
        => new(
            status,
            NormalizeReason(reason),
            authority,
            pauseBindCommand,
            drainCommand,
            dispositionCommand,
            routeDecision)
        {
            State = state,
        };

    private static string NormalizeReason(string? reason)
        => string.IsNullOrWhiteSpace(reason)
            ? "file_transfer_coordinator"
            : reason.Trim();
}
