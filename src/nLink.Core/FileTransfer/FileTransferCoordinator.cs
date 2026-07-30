using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

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
    TransportEpochDecision TargetTransportEpochDecision,
    string Reason)
{
    public FallbackTailYieldTransportEpochReauthorization? PendingReauthorization { get; init; }

    public bool IsDefault => PredecessorDecision.IsDefault ||
                             SourceRuntimeOwner.IsDefault ||
                             SourceRoutePublicationToken.IsDefault ||
                             SourceTransportEpoch <= 0 ||
                             TargetTransportEpoch <= SourceTransportEpoch ||
                             TargetTransportEpochDecision.IsDefault;
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
    TransportEpochDecision TargetTransportEpochDecision,
    RuntimeOwnerToken TargetRuntimeOwner,
    int TargetTerminalGeneration,
    int TargetLegGeneration,
    int TargetBridgeRecoveryGeneration,
    RoutePublicationToken? TargetRoutePublicationToken,
    BridgeRecoveryDecision? TargetBridgeRecoveryDecision,
    string? TargetDataSessionIdentity,
    long TargetDataSessionGeneration,
    string TargetCheckpointRequestIdSnapshot,
    int TargetCheckpointGeneration,
    FallbackCheckpointLease? TargetCheckpointLease,
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
                             TargetTransportEpochDecision.IsDefault ||
                             TargetTerminalGeneration < 0 ||
                             TargetLegGeneration <= 0 ||
                             TargetBridgeRecoveryGeneration < 0 ||
                             TargetRoutePublicationToken is not { IsDefault: false } ||
                             string.IsNullOrWhiteSpace(
                                 TargetDataSessionIdentity) ||
                             TargetDataSessionGeneration <= 0 ||
                             string.IsNullOrWhiteSpace(
                                 TargetCheckpointRequestIdSnapshot) ||
                             ReauthorizationGeneration <= 0;
}

internal enum FileTransferFallbackTailYieldPairingState
{
    None = 0,
    YieldIssued = 1,
    LocalProofObserved = 2,
    PeerProofObserved = 3,
    Paired = 4,
    ReleaseReserved = 5,
    ReleaseConsumed = 6,
    Invalidated = 7,
}

internal readonly record struct FileTransferFallbackTailYieldScope(
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    RuntimeOwnerToken RuntimeOwner,
    RoutePublicationToken? RoutePublication,
    FileTransferRoute Route,
    string RouteToken,
    int ProtocolVersion,
    int LiveRouteEpoch,
    int TerminalGeneration,
    int LegGeneration,
    long TransportEpoch,
    int BridgeRecoveryGeneration,
    string CheckpointRequestIdSnapshot,
    int CheckpointGeneration,
    string? DataSessionIdentity,
    long DataSessionGeneration,
    string OwnerKey,
    string PairKey)
{
    public bool IsDefault =>
        string.IsNullOrWhiteSpace(SessionId) ||
        string.IsNullOrWhiteSpace(TransferId) ||
        RuntimeOwner.IsDefault ||
        string.IsNullOrWhiteSpace(RouteToken) ||
        ProtocolVersion <= 0 ||
        LiveRouteEpoch <= 0 ||
        LegGeneration <= 0 ||
        TransportEpoch <= 0 ||
        string.IsNullOrWhiteSpace(CheckpointRequestIdSnapshot) ||
        string.IsNullOrWhiteSpace(OwnerKey) ||
        string.IsNullOrWhiteSpace(PairKey);

    public bool HasCompleteExecutionScope =>
        !IsDefault &&
        RoutePublication is { IsDefault: false } &&
        !string.IsNullOrWhiteSpace(DataSessionIdentity) &&
        DataSessionGeneration > 0;
}

internal readonly record struct FileTransferFallbackTailLocalProofReceipt(
    FallbackTailYieldDecision Decision,
    FileTransferFallbackTailYieldScope Scope,
    string ProofIdentity,
    string FrameFingerprint,
    long ObservedUtcMs)
{
    public bool IsDefault =>
        Decision.IsDefault ||
        Scope.IsDefault ||
        string.IsNullOrWhiteSpace(ProofIdentity) ||
        string.IsNullOrWhiteSpace(FrameFingerprint) ||
        ObservedUtcMs <= 0;
}

internal readonly record struct FileTransferFallbackTailPeerProof(
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    FileTransferAuthorityId AuthorityId,
    FileTransferAuthorityId? PredecessorAuthorityId,
    int YieldGeneration,
    string OwnerKey,
    string PairKey,
    int LiveRouteEpoch,
    int LegGeneration,
    int BridgeRecoveryGeneration,
    long TransportEpoch,
    string CheckpointRequestIdSnapshot,
    int CommittedChunkIndex,
    int HighestObservedChunkIndex,
    string FrameFingerprint)
{
    public bool IsDefault =>
        string.IsNullOrWhiteSpace(SessionId) ||
        string.IsNullOrWhiteSpace(TransferId) ||
        AuthorityId.IsDefault ||
        YieldGeneration <= 0 ||
        string.IsNullOrWhiteSpace(OwnerKey) ||
        string.IsNullOrWhiteSpace(PairKey) ||
        LiveRouteEpoch <= 0 ||
        LegGeneration <= 0 ||
        BridgeRecoveryGeneration < 0 ||
        TransportEpoch <= 0 ||
        string.IsNullOrWhiteSpace(CheckpointRequestIdSnapshot) ||
        CommittedChunkIndex < -1 ||
        HighestObservedChunkIndex <
            Math.Max(-1, CommittedChunkIndex - 1) ||
        string.IsNullOrWhiteSpace(FrameFingerprint);
}

internal readonly record struct FileTransferFallbackTailPeerProofReceipt(
    FileTransferFallbackTailPeerProof Proof,
    FileTransferFallbackTailYieldScope ObservationScope,
    string ObservationIdentity,
    long ObservedUtcMs)
{
    public bool IsDefault =>
        Proof.IsDefault ||
        ObservationScope.IsDefault ||
        string.IsNullOrWhiteSpace(ObservationIdentity) ||
        ObservedUtcMs <= 0;
}

internal readonly record struct FileTransferFallbackTailPairedReleaseReceipt(
    FileTransferFallbackTailLocalProofReceipt LocalProof,
    FileTransferFallbackTailPeerProofReceipt PeerProof,
    long PairingGeneration,
    RuntimeUnlockRetryReleaseKind ReleaseKind,
    string ReleaseOutcome)
{
    public FileTransferAuthorityId ReleaseAuthorityId =>
        LocalProof.Decision.AuthorityId;

    public int ReleaseGeneration =>
        LocalProof.Decision.YieldGeneration;

    public bool IsDefault =>
        LocalProof.IsDefault ||
        PeerProof.IsDefault ||
        PairingGeneration <= 0 ||
        ReleaseKind != RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield ||
        !string.Equals(
            ReleaseOutcome,
               FallbackTailYieldKind.Quiescent.ToString(),
               StringComparison.Ordinal);
}

internal readonly record struct
    FileTransferFallbackTailPairedReleaseCarryForwardReceipt(
        FallbackTailYieldDecision Decision,
        FileTransferFallbackTailPairedReleaseReceipt PairedRelease,
        FileTransferFallbackTailYieldScope SourceScope,
        FileTransferFallbackTailYieldScope TargetScope,
        string Reason)
{
    public bool IsDefault =>
        Decision.IsDefault ||
        PairedRelease.IsDefault ||
        SourceScope.IsDefault ||
        TargetScope.IsDefault ||
        TargetScope.TransportEpoch <= SourceScope.TransportEpoch ||
        string.IsNullOrWhiteSpace(Reason);
}

internal readonly record struct FileTransferFallbackTailPairedReleaseReservation(
    FileTransferFallbackTailPairedReleaseReceipt Release,
    RuntimeUnlockRetryDispatchAuthority RetryAuthority,
    long ReservationGeneration)
{
    public bool IsDefault =>
        Release.IsDefault ||
        RetryAuthority.IsDefault ||
        ReservationGeneration <= 0;
}

internal enum FileTransferFallbackTailRetiredOwnerDispositionStatus
{
    Unavailable = 0,
    Suppressed = 1,
    ScopeChanged = 2,
}

internal readonly record struct FileTransferFallbackTailRetiredOwnerDisposition(
    FileTransferFallbackTailRetiredOwnerDispositionStatus Status,
    FallbackTailYieldDecision? Decision,
    FileTransferFallbackTailYieldScope? RetiredScope,
    FileTransferFallbackTailYieldScope? CurrentScope,
    string Reason)
{
    public bool ShouldSuppress =>
        Status ==
        FileTransferFallbackTailRetiredOwnerDispositionStatus.Suppressed;
}

internal sealed record FileTransferFallbackTailYieldReauthorizationReceipt(
    FallbackTailYieldTransportEpochReauthorization Reauthorization)
{
    public bool IsDefault => Reauthorization.IsDefault;
}

internal readonly record struct FileTransferFallbackTailYieldOperation(
    FallbackTailYieldDecision Decision,
    FileTransferFallbackTailYieldScope Scope,
    FileTransferFallbackTailYieldPairingState PairingState,
    FileTransferFallbackTailLocalProofReceipt? LocalProof = null,
    FileTransferFallbackTailPeerProofReceipt? PeerProof = null,
    FileTransferFallbackTailPairedReleaseReceipt? PairedRelease = null,
    FileTransferFallbackTailPairedReleaseCarryForwardReceipt?
        PairedReleaseCarryForward = null,
    FileTransferFallbackTailYieldReauthorizationReceipt?
        SourceReauthorization = null,
    string? InvalidationReason = null)
{
    public bool IsDefault => Decision.IsDefault || Scope.IsDefault;
}

internal sealed record FileTransferFallbackTailYieldSubstate(
    FileTransferFallbackTailYieldOperation? CurrentOperation = null,
    FallbackTailYieldDecision? RetainedPredecessor = null,
    FallbackTailYieldTransportEpochReauthorization? PendingReauthorization = null,
    FileTransferFallbackTailPeerProofReceipt?
        PendingReauthorizationPeerProof = null,
    RuntimeUnlockRetryDispatchAuthorityRequest?
        PendingRetryRecheckRequest = null,
    FileTransferFallbackTailPairedReleaseReservation? CurrentReservation = null,
    FileTransferFallbackTailPairedReleaseReservation? LastConsumedReservation = null,
    RuntimeUnlockRetryDispatchInstallReceipt? LastConsumingInstallReceipt = null,
    FileTransferFallbackTailYieldOperation? LastTerminalOperation = null,
    FallbackTailYieldTransportEpochReauthorization?
        LastInvalidatedReauthorization = null,
    string? LastReauthorizationInvalidationReason = null,
    int LastYieldGeneration = 0,
    int LastReauthorizationGeneration = 0,
    long LastPairingGeneration = 0,
    long LastReservationGeneration = 0)
{
    internal static FileTransferFallbackTailYieldSubstate Empty { get; } =
        new();
}

internal enum FileTransferFallbackTailYieldTransitionStatus
{
    Accepted = 0,
    Idempotent = 1,
    Rejected = 2,
    Invalidated = 3,
}

internal readonly record struct FileTransferFallbackTailYieldTransitionDecision(
    FileTransferFallbackTailYieldTransitionStatus Status,
    string Reason,
    FileTransferCoordinatorState State)
{
    public bool Accepted =>
        Status is
            FileTransferFallbackTailYieldTransitionStatus.Accepted or
            FileTransferFallbackTailYieldTransitionStatus.Idempotent;

    public bool StateChanged =>
        Status is
            FileTransferFallbackTailYieldTransitionStatus.Accepted or
            FileTransferFallbackTailYieldTransitionStatus.Invalidated;

    public FileTransferFallbackTailPairedReleaseReceipt? PairedRelease =>
        State.PersistentState.FallbackTailYield.CurrentOperation?.PairedRelease;

    public RuntimeUnlockRetryDispatchAuthorityRequest? RetryRecheckRequest =>
        State.PersistentState.FallbackTailYield.PendingRetryRecheckRequest;
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
    FileTransferFallbackTailPairedReleaseReceipt?
        FallbackTailPairedReleaseReceipt = null)
    : FileTransferRouteTransitionProofBoundary
{
    internal FallbackTailYieldDecision? FallbackTailYieldDecision =>
        FallbackTailPairedReleaseReceipt?.LocalProof.Decision;

    public override bool IsDefault =>
        RuntimeUnlockAdmission.IsDefault ||
        PendingPreCommitProbe.IsDefault ||
        AcceptedLaunchBoundary.AdmissionToken != RuntimeUnlockAdmission ||
        LaunchGeneration <= 0 ||
        (EffectiveRetryInstallReceipt is { } receipt && receipt.IsDefault) ||
        (FallbackTailPairedReleaseReceipt is { } release &&
         release.IsDefault);
}

internal sealed record FileTransferFallbackTransitionProofBoundary(
    RouteCommitToken? SourceRouteCommit,
    PostTunaFallbackPeerProofEvidence PeerProofEvidence,
    PostTunaFallbackPeerProofFrameIdentity RetainedFrameIdentity,
    long TargetTransportEpoch,
    int TargetBridgeRecoveryGeneration,
    FileTransferAuthorityId? PredecessorTransitionAuthorityId = null)
    : FileTransferRouteTransitionProofBoundary
{
    public override bool IsDefault =>
        PeerProofEvidence is not { IsComplete: true, IsExactFrameBound: true } ||
        RetainedFrameIdentity is not { IsComplete: true } ||
        PeerProofEvidence.FrameIdentity != RetainedFrameIdentity ||
        TargetTransportEpoch <= 0 ||
        TargetBridgeRecoveryGeneration <= 0 ||
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
    FileTransferRouteTransitionAuthority Authority)
{
    public FileTransferRouteTransitionAuthority? PredecessorAuthority { get; init; }
}

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

internal readonly record struct FileTransferRouteTransitionRetainedFrameApplyCommand(
    FileTransferRouteTransitionAuthority Authority,
    RuntimeOwnerToken TargetRuntimeOwner,
    RoutePublicationToken TargetRoutePublication,
    PostTunaFallbackPeerProofFrameIdentity FrameIdentity);

internal enum FileTransferRouteTransitionRetainedFrameApplyStatus
{
    Applied,
    Idempotent,
    Rejected,
}

internal readonly record struct FileTransferRouteTransitionRetainedFrameApplyReceipt(
    FileTransferRouteTransitionRetainedFrameApplyCommand Command,
    FileTransferRouteTransitionRetainedFrameApplyStatus Status,
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
    FileTransferRouteTransitionDispositionReceipt? DispositionReceipt = null,
    FileTransferRouteTransitionRetainedFrameApplyCommand? RetainedFrameApplyCommand = null,
    FileTransferRouteTransitionRetainedFrameApplyReceipt? RetainedFrameApplyReceipt = null);

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

internal readonly record struct FileTransferRouteTransitionPauseOwnership(
    FileTransferRouteTransitionAuthority Authority,
    FileTransferRouteTransitionState State,
    bool RetainedFrameHandoffPending,
    bool DispositionHandoffPending);

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
    FileTransferRouteTransitionRetainedFrameApplyCommand?
        RetainedFrameApplyCommand = null,
    FileTransferCoordinatorDecision? RouteDecision = null)
{
    public FileTransferCoordinatorState State { get; init; } = null!;
}

internal enum RuntimeUnlockRetryLifecycleAttemptState
{
    None = 0,
    Authorized = 1,
    Staged = 2,
    Installed = 3,
    AliasBound = 4,
    SendInFlight = 5,
    Settled = 6,
    Closed = 7,
    Invalidated = 8,
}

internal readonly record struct RuntimeUnlockRetryLifecycleAttemptSnapshot(
    RuntimeUnlockRetryDispatchAuthorityRequest Request,
    RuntimeUnlockRetryDispatchAuthority Authority,
    RuntimeUnlockRetryLifecycleAttemptState State,
    RuntimeUnlockRetryDispatchDecisionStageStatus? StageStatus = null,
    RuntimeUnlockRetryDispatchStageDisposition StageDisposition =
        RuntimeUnlockRetryDispatchStageDisposition.Unknown,
    long StagedUtcMs = 0,
    long StageExpiresUtcMs = 0,
    RuntimeUnlockRetryDispatchInstallReceipt? InstallReceipt = null,
    long DispatchDeadlineUtcMs = 0,
    long OfferGeneration = 0,
    string? OfferNonce = null,
    RuntimeUnlockRetryDispatchSettlementReport? Settlement = null,
    FileTransferAuthorityId? SuccessorAuthorityId = null,
    bool DispatchObserved = false)
{
    public bool IsDefault => Request.IsDefault || Authority.IsDefault;

    public bool IsInstalled =>
        InstallReceipt is { IsDefault: false } &&
        State is
            RuntimeUnlockRetryLifecycleAttemptState.Installed or
            RuntimeUnlockRetryLifecycleAttemptState.AliasBound or
            RuntimeUnlockRetryLifecycleAttemptState.SendInFlight or
            RuntimeUnlockRetryLifecycleAttemptState.Settled;

    public bool IsTerminal =>
        State is
            RuntimeUnlockRetryLifecycleAttemptState.Settled or
            RuntimeUnlockRetryLifecycleAttemptState.Closed or
            RuntimeUnlockRetryLifecycleAttemptState.Invalidated;
}

internal readonly record struct RuntimeUnlockRetryLifecycleBoundedClosure(
    RuntimeUnlockRetryLifecycleAttemptSnapshot FinalAttempt,
    RuntimeUnlockRetryDispatchAuthorityRequest RejectedSuccessorRequest,
    long ClosureGeneration,
    string Reason)
{
    public bool IsDefault =>
        FinalAttempt.IsDefault ||
        RejectedSuccessorRequest.IsDefault ||
        ClosureGeneration <= 0 ||
        string.IsNullOrWhiteSpace(Reason);
}

internal enum RuntimeUnlockRetryLifecycleRepairSettlementReleaseState
{
    Available = 0,
    Consumed = 1,
}

internal readonly record struct RuntimeUnlockRetryLifecycleRepairSettlementRelease(
    FileTransferRegularV4FrontierRepairSettlement Settlement,
    FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt
        SettlementApplyReceipt,
    RuntimeUnlockRetryLifecycleRepairSettlementReleaseState State,
    RuntimeUnlockRetryDispatchAuthority? ConsumingAuthority = null,
    RuntimeUnlockRetryDispatchInstallReceipt? ConsumingInstallReceipt = null)
{
    public bool IsDefault =>
        Settlement is null ||
        Settlement.AuthorityId.IsDefault ||
        SettlementApplyReceipt is null ||
        SettlementApplyReceipt.Command.Settlement != Settlement ||
        SettlementApplyReceipt.Status is not (
            FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus
                .Applied or
            FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus
                .Idempotent) ||
        State is not (
            RuntimeUnlockRetryLifecycleRepairSettlementReleaseState.Available or
            RuntimeUnlockRetryLifecycleRepairSettlementReleaseState.Consumed) ||
        (State == RuntimeUnlockRetryLifecycleRepairSettlementReleaseState.Available
            ? ConsumingAuthority is not null ||
              ConsumingInstallReceipt is not null
            : ConsumingAuthority is not { IsDefault: false } authority ||
              ConsumingInstallReceipt is not
                  { IsDefault: false } installReceipt ||
              installReceipt.Authority != authority ||
              installReceipt.Authority.ReleaseKind !=
                  RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement ||
              installReceipt.Authority.ReleaseAuthorityId !=
                  Settlement.AuthorityId ||
              installReceipt.Authority.ReleaseGeneration !=
                  Settlement.DecisionGeneration ||
              !string.Equals(
                  installReceipt.Authority.ReleaseOutcome,
                  Settlement.Outcome.ToString(),
                  StringComparison.Ordinal));
}

internal sealed record RuntimeUnlockRetryLifecycleSubstate(
    RuntimeUnlockRetryLifecycleAttemptSnapshot? PendingAttempt = null,
    RuntimeUnlockRetryLifecycleAttemptSnapshot? InstalledAttempt = null,
    RuntimeUnlockRetryLifecycleAttemptSnapshot? RetainedPredecessor = null,
    RuntimeUnlockRetryLifecycleAttemptSnapshot? LastTerminalAttempt = null,
    RuntimeUnlockRetryLifecycleBoundedClosure? BoundedClosure = null,
    RuntimeUnlockRetryLifecycleRepairSettlementRelease?
        RepairSettlementRelease = null,
    RuntimeUnlockRetryDispatchSettlementReport? LastSettlement = null,
    int LastAuthorityGeneration = 0,
    long LastInstallGeneration = 0,
    long LastClosureGeneration = 0,
    RuntimeUnlockRetryDispatchScopeInvalidationCommand?
        PendingScopeInvalidationCommand = null,
    RuntimeUnlockRetryDispatchScopeInvalidationReceipt?
        LastScopeInvalidationReceipt = null,
    long LastScopeInvalidationGeneration = 0,
    ImmutableArray<RuntimeUnlockRetryLifecycleAttemptSnapshot> AttemptHistory = default)
{
    internal static RuntimeUnlockRetryLifecycleSubstate Empty { get; } = new();

    public RuntimeUnlockRetryDispatchAuthority? CurrentAuthorizedAuthority =>
        PendingAttempt?.Authority ?? InstalledAttempt?.Authority;

    public RuntimeUnlockRetryDispatchAuthority? CurrentInstalledAuthority =>
        InstalledAttempt is { IsInstalled: true } attempt
            ? attempt.Authority
            : null;
}

internal enum RuntimeUnlockRetryLifecycleTransitionStatus
{
    Accepted = 0,
    Idempotent = 1,
    Expired = 2,
    Rejected = 3,
}

internal readonly record struct RuntimeUnlockRetryLifecycleTransitionDecision(
    RuntimeUnlockRetryLifecycleTransitionStatus Status,
    string Reason)
{
    public FileTransferCoordinatorState State { get; init; } = null!;

    public RuntimeUnlockRetryDispatchSettlementDecision?
        PredecessorSettlementDecision { get; init; }

    public bool Accepted =>
        Status is
            RuntimeUnlockRetryLifecycleTransitionStatus.Accepted or
            RuntimeUnlockRetryLifecycleTransitionStatus.Idempotent;
}

internal readonly record struct RuntimeUnlockRetryLifecycleInstallReceiptDecision(
    RuntimeUnlockRetryLifecycleTransitionStatus Status,
    string Reason,
    RuntimeUnlockRetryDispatchInstallReceipt? Receipt = null)
{
    public FileTransferCoordinatorState State { get; init; } = null!;

    public bool Accepted =>
        Status is
            RuntimeUnlockRetryLifecycleTransitionStatus.Accepted or
            RuntimeUnlockRetryLifecycleTransitionStatus.Idempotent &&
        Receipt is { IsDefault: false };
}

internal readonly record struct RuntimeUnlockRetryLifecycleDeadlineDecision(
    RuntimeUnlockRetryLifecycleTransitionStatus Status,
    string Reason,
    RuntimeUnlockRetryDispatchSettlementReport? Settlement = null)
{
    public FileTransferCoordinatorState State { get; init; } = null!;

    public bool SettlementIssued =>
        Status == RuntimeUnlockRetryLifecycleTransitionStatus.Accepted &&
        Settlement is { IsDefault: false };
}

internal readonly record struct RuntimeUnlockRetryLifecycleScopeInvalidationDecision(
    RuntimeUnlockRetryLifecycleTransitionStatus Status,
    string Reason,
    RuntimeUnlockRetryDispatchScopeInvalidationCommand? Command = null)
{
    public FileTransferCoordinatorState State { get; init; } = null!;

    public bool Accepted =>
        Status is
            RuntimeUnlockRetryLifecycleTransitionStatus.Accepted or
            RuntimeUnlockRetryLifecycleTransitionStatus.Idempotent;
}

internal enum RuntimeUnlockRetryDispatchSuccessorDecisionStatus
{
    Requested = 0,
    Idempotent = 1,
    ChainClosed = 2,
    Held = 3,
    Rejected = 4,
}

internal readonly record struct RuntimeUnlockRetryDispatchSuccessorDecision(
    RuntimeUnlockRetryDispatchSuccessorDecisionStatus Status,
    string Reason,
    RuntimeUnlockRetryDispatchSuccessorEvaluation Evaluation,
    RuntimeUnlockRetryDispatchAuthorityRequest? Request = null,
    RuntimeUnlockRetryDispatchDecision? AuthorizationDecision = null)
{
    public FileTransferCoordinatorState State { get; init; } = null!;

    public bool Requested =>
        (Status is
             RuntimeUnlockRetryDispatchSuccessorDecisionStatus.Requested or
             RuntimeUnlockRetryDispatchSuccessorDecisionStatus.Idempotent) &&
        Request is not null &&
        AuthorizationDecision is { Granted: true };
}

internal readonly record struct FileTransferReceiveRecoveryHealthEvaluationEvidence(
    FileTransferBridgeRecoveryEvaluationKind Kind,
    string ObservationKind,
    string Reason,
    long EvidenceGeneration,
    long ObservedUtcMs,
    int ConsecutiveObservations,
    long FramesSentSinceLast,
    long MessagesReceivedSinceLast,
    long ControlLastReceivedAgeMs,
    long BulkLastReceivedAgeMs)
{
    public bool IsDefault =>
        Kind == FileTransferBridgeRecoveryEvaluationKind.Unknown ||
        string.IsNullOrWhiteSpace(ObservationKind) ||
        string.IsNullOrWhiteSpace(Reason) ||
        EvidenceGeneration <= 0 ||
        ObservedUtcMs <= 0 ||
        ConsecutiveObservations < 0 ||
        FramesSentSinceLast < 0 ||
        MessagesReceivedSinceLast < 0;
}

internal readonly record struct FileTransferReceiveRecoveryObservation(
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    FileTransferRoute Route,
    string RouteToken,
    int ProtocolVersion,
    int LiveRouteEpoch,
    RuntimeOwnerToken RuntimeOwner,
    RoutePublicationToken RoutePublication,
    RouteCommitToken? RouteCommit,
    int TerminalGeneration,
    int LegGeneration,
    long TransportEpoch,
    int BridgeRecoveryGeneration,
    string? CheckpointRequestId,
    string DataSessionIdentity,
    long DataSessionGeneration,
    string Source,
    string Reason,
    string ObservationIdentity,
    long ObservationGeneration,
    long ObservedUtcMs)
{
    public FileTransferReceiveRecoveryHealthEvaluationEvidence?
        HealthEvaluationEvidence { get; init; }

    public bool IsDefault =>
        string.IsNullOrWhiteSpace(SessionId) ||
        string.IsNullOrWhiteSpace(TransferId) ||
        string.IsNullOrWhiteSpace(RouteToken) ||
        RuntimeOwner.IsDefault ||
        RoutePublication.IsDefault ||
        LegGeneration <= 0 ||
        TransportEpoch < 0 ||
        BridgeRecoveryGeneration < 0 ||
        string.IsNullOrWhiteSpace(DataSessionIdentity) ||
        DataSessionGeneration <= 0 ||
        string.IsNullOrWhiteSpace(Source) ||
        string.IsNullOrWhiteSpace(Reason) ||
        string.IsNullOrWhiteSpace(ObservationIdentity) ||
        ObservationGeneration <= 0 ||
        ObservedUtcMs <= 0 ||
        HealthEvaluationEvidence is { IsDefault: true };
}

internal readonly record struct FileTransferReceiveRecoveryOperationAuthority(
    FileTransferReceiveRecoveryObservation Observation,
    BridgeRecoveryDecision BridgeRecoveryDecision,
    FileTransferBridgeRecoveryAuthority CompatibilityBridgeAuthority,
    RecoveryLivenessLease? RecoveryLivenessLease,
    FileTransferBridgeRecoveryExecutionCommand ExecutionCommand,
    long OperationGeneration,
    FileTransferAuthorityId AuthorityId)
{
    public FileTransferAuthorityId? PredecessorAuthorityId { get; init; }

    public long PredecessorOperationGeneration { get; init; }

    public bool IsDefault =>
        Observation.IsDefault ||
        BridgeRecoveryDecision.IsDefault ||
        !BridgeRecoveryDecision.Accepted ||
        CompatibilityBridgeAuthority is null ||
        !string.Equals(
            CompatibilityBridgeAuthority.SessionId,
            Observation.SessionId,
            StringComparison.Ordinal) ||
        !string.Equals(
            CompatibilityBridgeAuthority.TransferId,
            Observation.TransferId,
            StringComparison.Ordinal) ||
        CompatibilityBridgeAuthority.Direction != Observation.Direction ||
        !string.Equals(
            CompatibilityBridgeAuthority.RouteToken,
            Observation.RouteToken,
            StringComparison.Ordinal) ||
        CompatibilityBridgeAuthority.ProtocolVersion !=
            Observation.ProtocolVersion ||
        CompatibilityBridgeAuthority.LiveRouteEpoch !=
            Observation.LiveRouteEpoch ||
        CompatibilityBridgeAuthority.RuntimeGeneration !=
            Observation.RuntimeOwner.RuntimeGeneration ||
        CompatibilityBridgeAuthority.TerminalGeneration !=
            Observation.TerminalGeneration ||
        CompatibilityBridgeAuthority.TransferLegGeneration !=
            Observation.LegGeneration ||
        CompatibilityBridgeAuthority.TransportEpoch !=
            Observation.TransportEpoch ||
        CompatibilityBridgeAuthority.BridgeRecoveryGeneration !=
            Observation.BridgeRecoveryGeneration ||
        CompatibilityBridgeAuthority.BridgeDecisionGeneration !=
            BridgeRecoveryDecision.BridgeDecisionGeneration ||
        !string.Equals(
            CompatibilityBridgeAuthority.RecoveryKind,
            BridgeRecoveryDecision.RecoveryKind.ToString(),
            StringComparison.Ordinal) ||
        !string.Equals(
            CompatibilityBridgeAuthority.Reason,
            BridgeRecoveryDecision.Reason,
            StringComparison.Ordinal) ||
        ExecutionCommand.IsDefault ||
        ExecutionCommand.Decision != BridgeRecoveryDecision ||
        ExecutionCommand.RecoveryLivenessLease != RecoveryLivenessLease ||
        OperationGeneration <= 0 ||
        AuthorityId.IsDefault ||
        (PredecessorAuthorityId is null) !=
            (PredecessorOperationGeneration == 0) ||
        PredecessorAuthorityId is { IsDefault: true } ||
        PredecessorOperationGeneration < 0;
}

internal enum FileTransferReceiveRecoveryExecutionState
{
    Issued = 0,
    Running = 1,
    Completed = 2,
    Exhausted = 3,
    Discarded = 4,
    Invalidated = 5,
}

internal enum FileTransferReceiveRecoveryClosureState
{
    None = 0,
    Retained = 1,
    Adopted = 2,
    Reserved = 3,
    Consumed = 4,
}

internal readonly record struct FileTransferReceiveRecoveryProof(
    FileTransferReceiveRecoveryOperationAuthority Authority,
    string ProofIdentity,
    string Reason,
    long ObservedUtcMs)
{
    public bool IsDefault =>
        Authority.IsDefault ||
        string.IsNullOrWhiteSpace(ProofIdentity) ||
        string.IsNullOrWhiteSpace(Reason) ||
        ObservedUtcMs <= 0;
}

internal sealed record
    FileTransferReceiveRecoveryPeerReachabilityObservation(
        FileTransferBridgeRecoveryExecutionCommand ExecutionCommand,
        long SubmissionEpoch,
        long ProcessEpoch,
        int ProcessId,
        long ProcessSpawnTicks,
        string HeartbeatIdentity,
        string ReceiveResumedIdentity,
        long ObservedUtcMs,
        string Source)
{
    public bool IsDefault =>
        ExecutionCommand.IsDefault ||
        SubmissionEpoch <= 0 ||
        ProcessEpoch <= 0 ||
        ProcessId <= 0 ||
        ProcessSpawnTicks <= 0 ||
        string.IsNullOrWhiteSpace(HeartbeatIdentity) ||
        string.IsNullOrWhiteSpace(ReceiveResumedIdentity) ||
        ObservedUtcMs <= 0 ||
        string.IsNullOrWhiteSpace(Source);
}

internal sealed record
    FileTransferReceiveRecoveryProofStatePublicationCommand(
        FileTransferReceiveRecoveryOperationAuthority Authority,
        long ClosureGeneration,
        FileTransferReceiveRecoveryPeerReachabilityObservation Observation,
        long PublicationGeneration)
{
    public bool IsDefault =>
        Authority.IsDefault ||
        ClosureGeneration <= 0 ||
        Observation.IsDefault ||
        Observation.ExecutionCommand != Authority.ExecutionCommand ||
        PublicationGeneration <= 0;
}

internal enum FileTransferReceiveRecoveryProofStatePublicationOutcome
{
    Delivered = 0,
    Held = 1,
    Rejected = 2,
}

internal sealed record
    FileTransferReceiveRecoveryProofStatePublicationReceipt(
        FileTransferReceiveRecoveryProofStatePublicationCommand Command,
        FileTransferReceiveRecoveryProofStatePublicationOutcome Outcome,
        string FrameFingerprint,
        string Reason)
{
    public bool IsDefault =>
        Command.IsDefault ||
        string.IsNullOrWhiteSpace(FrameFingerprint) ||
        string.IsNullOrWhiteSpace(Reason);
}

internal sealed record FileTransferReceiveRecoveryProofStatePublicationSubstate(
    FileTransferReceiveRecoveryPeerReachabilityObservation?
        PendingObservation = null,
    FileTransferReceiveRecoveryProofStatePublicationCommand?
        PendingCommand = null,
    FileTransferReceiveRecoveryProofStatePublicationReceipt?
        LastReceipt = null,
    long LastGeneration = 0)
{
    internal static
        FileTransferReceiveRecoveryProofStatePublicationSubstate Empty { get; }
        = new();
}

internal readonly record struct FileTransferReceiveRecoveryOperation(
    FileTransferReceiveRecoveryOperationAuthority Authority,
    FileTransferReceiveRecoveryExecutionState ExecutionState,
    FileTransferBridgeRecoveryExecutionStartStatus? ExecutionStartStatus = null,
    string? ExecutionStartReason = null,
    FileTransferBridgeRecoveryExecutionStartResult? ExecutionStartResult = null,
    FileTransferBridgeRecoveryExecutionReceipt? ExecutionReceipt = null,
    FileTransferReceiveRecoveryProof? ReceiveProof = null,
    RecoveryLivenessLease? RetiredRecoveryLivenessLease = null,
    FileTransferReceiveRecoveryClosureState ClosureState =
        FileTransferReceiveRecoveryClosureState.None,
    long ClosureGeneration = 0,
    RuntimeUnlockRetryDispatchAuthorityRequest? HeldRetryRequest = null,
    RuntimeUnlockRetryDispatchAuthorityRequest? ReservedRetryRequest = null,
    RuntimeUnlockRetryDispatchAuthority? ReservedRetryAuthority = null,
    RuntimeUnlockRetryDispatchInstallReceipt? ConsumingInstallReceipt = null,
    string? InvalidationReason = null,
    long LivenessDeferralDeadlineUtcMs = 0,
    FileTransferRecoveryLivenessDeferralGrant? LivenessDeferralGrant = null)
{
    public long ExecutionReceiptObservedUtcMs { get; init; }

    public bool IsDefault => Authority.IsDefault;

    public FileTransferBridgeRecoveryExecutionOutcome? LifecycleOutcome =>
        ExecutionReceipt?.Outcome;

    public bool IsTerminal =>
        ExecutionState is
            FileTransferReceiveRecoveryExecutionState.Completed or
            FileTransferReceiveRecoveryExecutionState.Exhausted or
            FileTransferReceiveRecoveryExecutionState.Discarded or
            FileTransferReceiveRecoveryExecutionState.Invalidated;
}

internal sealed record
    RuntimeUnlockInitialActivationReceiveRecoveryHold(
        RuntimeUnlockInitialActivationReceiveRecoveryHoldRequest Request,
        FileTransferReceiveRecoveryOperationAuthority
            ReceiveRecoveryAuthority,
        long HoldGeneration,
        RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus Status,
        string Reason,
        RuntimeUnlockInitialActivationReceiveRecoveryHoldInvalidationKind
            InvalidationKind =
                RuntimeUnlockInitialActivationReceiveRecoveryHoldInvalidationKind
                    .Unspecified)
{
    public bool IsDefault =>
        Request.IsDefault ||
        ReceiveRecoveryAuthority.IsDefault ||
        HoldGeneration <= 0 ||
        string.IsNullOrWhiteSpace(Reason);
}

internal readonly record struct FileTransferReceiveRecoverySubstate(
    FileTransferReceiveRecoveryOperation? CurrentOperation = null,
    FileTransferReceiveRecoveryOperation? LastTerminalOperation = null,
    long LastOperationGeneration = 0,
    long LastClosureGeneration = 0,
    RuntimeUnlockInitialActivationReceiveRecoveryHold?
        CurrentInitialActivationHold = null,
    RuntimeUnlockInitialActivationReceiveRecoveryHold?
        LastInitialActivationHold = null,
    long LastInitialActivationHoldGeneration = 0,
    RuntimeUnlockInitialActivationOfferEnvelopeRefreshCommand?
        PendingInitialActivationEnvelopeRefresh = null,
    RuntimeUnlockInitialActivationOfferEnvelopeRefreshReceipt?
        LastInitialActivationEnvelopeRefreshReceipt = null,
    long LastInitialActivationEnvelopeRefreshGeneration = 0,
    FileTransferReceiveRecoveryProofStatePublicationSubstate?
        ProofStatePublication = null)
{
    internal static FileTransferReceiveRecoverySubstate Empty => default;
}

internal enum FileTransferReceiveRecoveryTransitionStatus
{
    Accepted = 0,
    Idempotent = 1,
    Rejected = 2,
}

internal readonly record struct FileTransferReceiveRecoveryDecision(
    FileTransferReceiveRecoveryTransitionStatus Status,
    string Reason,
    FileTransferReceiveRecoveryOperationAuthority? Authority = null)
{
    public FileTransferCoordinatorState State { get; init; } = null!;

    public RuntimeUnlockRetryDispatchDecision? RetryDispatchDecision
    {
        get;
        init;
    }

    public RuntimeUnlockInitialActivationReceiveRecoveryHoldDecision?
        InitialActivationHoldDecision
    {
        get;
        init;
    }

    public FileTransferReceiveRecoveryProofStatePublicationCommand?
        ProofStatePublicationCommand
    {
        get;
        init;
    }

    public bool Accepted =>
        Status is
            FileTransferReceiveRecoveryTransitionStatus.Accepted or
            FileTransferReceiveRecoveryTransitionStatus.Idempotent;
}

internal readonly record struct
    RuntimeUnlockInitialActivationReceiveRecoveryHoldCoordinatorDecision(
        RuntimeUnlockInitialActivationReceiveRecoveryHoldDecision Decision)
{
    public FileTransferCoordinatorState State { get; init; } = null!;
}

internal readonly record struct
    RuntimeUnlockInitialActivationOfferEnvelopeRefreshCoordinatorDecision(
        RuntimeUnlockInitialActivationOfferEnvelopeRefreshDecision Decision)
{
    public FileTransferCoordinatorState State { get; init; } = null!;
}

internal readonly record struct FileTransferCoordinatorPersistentState(
    RegularV4FrontierRepairPressureSubstate RegularV4FrontierRepairPressure = default,
    FileTransferRouteTransitionSubstate FileTransferRouteTransition = default,
    FileTransferReceiveRecoverySubstate FileTransferReceiveRecovery = default)
{
    private readonly RuntimeUnlockRetryLifecycleSubstate?
        runtimeUnlockRetryLifecycle;
    private readonly FileTransferFallbackTailYieldSubstate?
        fallbackTailYield;

    internal RuntimeUnlockRetryLifecycleSubstate RuntimeUnlockRetryLifecycle
    {
        get =>
            runtimeUnlockRetryLifecycle ??
            RuntimeUnlockRetryLifecycleSubstate.Empty;
        init => runtimeUnlockRetryLifecycle = value;
    }

    internal FileTransferFallbackTailYieldSubstate FallbackTailYield
    {
        get =>
            fallbackTailYield ??
            FileTransferFallbackTailYieldSubstate.Empty;
        init => fallbackTailYield = value;
    }

    internal static FileTransferCoordinatorPersistentState Empty => default;
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
    FileTransferReceiveRecoveryOperationAuthority ReceiveRecoveryOperation)
{
    public BridgeRecoveryDecision BridgeRecoveryDecision =>
        ReceiveRecoveryOperation.BridgeRecoveryDecision;

    public RecoveryLivenessLease RecoveryLivenessLease =>
        ReceiveRecoveryOperation.RecoveryLivenessLease ??
        throw new InvalidOperationException(
            "The regular-V4 pressure handoff requires a recovery lease.");

    public FileTransferBridgeRecoveryExecutionCommand
        BridgeRecoveryExecutionCommand =>
            ReceiveRecoveryOperation.ExecutionCommand;
}

internal readonly record struct RegularV4FrontierRepairPressureRecoveryHandoffDecision(
    RegularV4FrontierRepairPressureRecoveryHandoffStatus Status,
    string Reason,
    RegularV4FrontierRepairPressureRecoveryHandoffTransition? Transition)
{
    public FileTransferCoordinatorState State { get; init; } = null!;

    public RuntimeUnlockInitialActivationReceiveRecoveryHoldDecision?
        InitialActivationHoldDecision
    {
        get;
        init;
    }
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
    long TargetTransportEpoch,
    int BridgeRecoveryGeneration,
    FileTransferRoute TargetRoute,
    int TargetProtocolVersion,
    FileTransferTransportKind TargetTransport,
    RuntimeOwnerToken RuntimeOwner)
{
    public bool IsDefault => TransportDecisionGeneration <= 0 ||
                             RuntimeOwner.IsDefault;
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
    RepairReplayDecision? CurrentRepairReplayDecision = null,
    int LastRepairReplayGeneration = 0,
    TransportEpochDecision? CurrentTransportEpochDecision = null,
    int LastTransportEpochDecisionGeneration = 0,
    RoutePublicationToken? CurrentRoutePublicationToken = null,
    int LastRoutePublicationGeneration = 0,
    RuntimeUnlockPendingPreCommitProbeRequest? CurrentRuntimeUnlockPendingPreCommitProbe = null,
    int LastRuntimeUnlockPendingPreCommitProbeGeneration = 0,
    FileTransferCoordinatorPersistentState PersistentState = default,
    string? CurrentDataSessionIdentity = null,
    long CurrentDataSessionGeneration = 0)
{
    public FileTransferRouteRuntimeDescriptor RouteRuntime => RouteSelection.RuntimeDescriptor;

    internal BridgeRecoveryDecision? CurrentBridgeRecoveryDecision =>
        PersistentState.FileTransferReceiveRecovery
            .CurrentOperation?.Authority.BridgeRecoveryDecision;

    internal int LastBridgeRecoveryDecisionGeneration =>
        Math.Max(
            PersistentState.FileTransferReceiveRecovery
                .CurrentOperation?.Authority.BridgeRecoveryDecision
                .BridgeDecisionGeneration ?? 0,
            PersistentState.FileTransferReceiveRecovery
                .LastTerminalOperation?.Authority.BridgeRecoveryDecision
                .BridgeDecisionGeneration ?? 0);

    internal RecoveryLivenessLease? CurrentRecoveryLivenessLease =>
        PersistentState.FileTransferReceiveRecovery.CurrentOperation is
        {
            ExecutionReceipt: null,
        } operation
            ? operation.Authority.RecoveryLivenessLease
            : null;

    internal int LastRecoveryLivenessLeaseGeneration =>
        Math.Max(
            PersistentState.FileTransferReceiveRecovery
                .CurrentOperation?.Authority.RecoveryLivenessLease
                ?.LeaseGeneration ?? 0,
            PersistentState.FileTransferReceiveRecovery
                .LastTerminalOperation?.Authority.RecoveryLivenessLease
                ?.LeaseGeneration ?? 0);

    internal RecoveryLivenessLease? LastRetiredRecoveryLivenessLease =>
        PersistentState.FileTransferReceiveRecovery
            .CurrentOperation?.RetiredRecoveryLivenessLease ??
        PersistentState.FileTransferReceiveRecovery
            .LastTerminalOperation?.RetiredRecoveryLivenessLease;

    internal FileTransferBridgeRecoveryExecutionCommand?
        CurrentBridgeRecoveryExecutionCommand =>
        PersistentState.FileTransferReceiveRecovery.CurrentOperation is
        {
            ExecutionState:
                FileTransferReceiveRecoveryExecutionState.Issued or
                FileTransferReceiveRecoveryExecutionState.Running,
        } operation
            ? operation.Authority.ExecutionCommand
            : null;

    internal FileTransferBridgeRecoveryExecutionState?
        CurrentBridgeRecoveryExecutionState =>
        PersistentState.FileTransferReceiveRecovery
            .CurrentOperation?.ExecutionState switch
            {
                FileTransferReceiveRecoveryExecutionState.Issued =>
                    FileTransferBridgeRecoveryExecutionState.Issued,
                FileTransferReceiveRecoveryExecutionState.Running =>
                    FileTransferBridgeRecoveryExecutionState.Running,
                _ => null,
            };

    internal FileTransferBridgeRecoveryExecutionReceipt?
        LastBridgeRecoveryExecutionReceipt =>
        PersistentState.FileTransferReceiveRecovery
            .CurrentOperation?.ExecutionReceipt ??
        PersistentState.FileTransferReceiveRecovery
            .LastTerminalOperation?.ExecutionReceipt;

    internal RuntimeUnlockRetryDispatchAuthority?
        CurrentRuntimeUnlockRetryDispatchAuthority =>
        PersistentState.RuntimeUnlockRetryLifecycle.CurrentAuthorizedAuthority;

    internal int LastRuntimeUnlockRetryDispatchAuthorityGeneration =>
        PersistentState.RuntimeUnlockRetryLifecycle.LastAuthorityGeneration;

    internal RuntimeUnlockRetryDispatchSettlementReport?
        LastRuntimeUnlockRetryDispatchSettlement =>
        PersistentState.RuntimeUnlockRetryLifecycle.LastSettlement;
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
    long? TargetTransportEpochId = null,
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

    public RuntimeUnlockInitialActivationReceiveRecoveryHoldDecision?
        InitialActivationHoldDecision
    {
        get;
        init;
    }
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
    private const long ReceiveRecoveryLivenessDeferralMs = 90_000;
    private const long ReceiveRecoveryLivenessMaxDeferralMs = 210_000;

    internal const int RuntimeUnlockRetryMissingDispatchSuccessorLimit = 1;
    internal const long RuntimeUnlockRetryDispatchDeadlineMilliseconds = 15_000;
    internal static TimeSpan? RuntimeUnlockRetryDispatchDeadlineOverrideForTests;

    private static readonly TimeSpan InitialTunaReadinessEvidenceMaxAge = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SessionReactivationInitialTunaReadinessEvidenceMaxAge = TimeSpan.FromMinutes(30);

    internal static FileTransferRouteTransitionSubstate
        GetFileTransferRouteTransitionSubstate(FileTransferCoordinatorState state)
        => state.PersistentState.FileTransferRouteTransition;

    internal static RuntimeUnlockRetryLifecycleSubstate
        GetRuntimeUnlockRetryLifecycle(FileTransferCoordinatorState state)
        => state.PersistentState.RuntimeUnlockRetryLifecycle;

    internal static bool TryPrepareRuntimeUnlockRetryDispatchInitialRequest(
        FileTransferCoordinatorState state,
        RuntimeUnlockRetryDispatchInitialEvaluation evaluation,
        out RuntimeUnlockRetryDispatchAuthorityRequest request,
        out string reason)
    {
        request = default;
        reason = "accepted";
        if (evaluation.IsDefault)
        {
            reason = "retry_dispatch_initial_evaluation_missing";
            return false;
        }

        var lifecycle = GetRuntimeUnlockRetryLifecycle(state);
        if (lifecycle.PendingScopeInvalidationCommand is not null)
        {
            reason = "retry_dispatch_scope_invalidation_pending";
            return false;
        }

        var currentAttempt =
            lifecycle.PendingAttempt ??
            lifecycle.InstalledAttempt ??
            lifecycle.RetainedPredecessor;
        if (currentAttempt is { } existing)
        {
            if (existing.Request.DispatchAttemptGeneration != 1 ||
                existing.Request.MissingDispatchSuccessorGeneration != 0 ||
                existing.Request.PredecessorAuthorityId is not null ||
                existing.Request.RequestActivePredecessorSupersession ||
                !RuntimeUnlockRetryDispatchInitialEvaluationMatchesRequest(
                    evaluation,
                    existing.Request))
            {
                reason = "retry_dispatch_initial_attempt_already_current";
                return false;
            }

            request = existing.Request;
            reason = "idempotent";
            return true;
        }

        request = new RuntimeUnlockRetryDispatchAuthorityRequest(
            evaluation.SessionId,
            evaluation.TransferId,
            evaluation.Direction,
            evaluation.SourcePublicationAuthority,
            evaluation.RecoveryContractGeneration,
            evaluation.RetiredOfferGeneration,
            DispatchAttemptGeneration: 1,
            evaluation.RetryReason,
            evaluation.RecoveryReason,
            evaluation.RegularV4LivenessGeneration,
            evaluation.BridgeDecisionGeneration,
            PredecessorAuthorityId: null,
            RequestActivePredecessorSupersession: false,
            MissingDispatchSuccessorGeneration: 0)
        {
            FallbackPairedTailYieldEvidence =
                evaluation.FallbackPairedTailYieldEvidence,
            ListenerRearmRequirement = evaluation.ListenerRearmRequirement,
        };
        return true;
    }

    private static bool RuntimeUnlockRetryDispatchInitialEvaluationMatchesRequest(
        RuntimeUnlockRetryDispatchInitialEvaluation evaluation,
        RuntimeUnlockRetryDispatchAuthorityRequest request)
        => string.Equals(
               evaluation.SessionId,
               request.SessionId,
               StringComparison.Ordinal) &&
           string.Equals(
               evaluation.TransferId,
               request.TransferId,
               StringComparison.Ordinal) &&
           evaluation.Direction == request.Direction &&
           evaluation.SourcePublicationAuthority ==
               request.SourcePublicationAuthority &&
           evaluation.RecoveryContractGeneration ==
               request.RecoveryContractGeneration &&
           evaluation.RetiredOfferGeneration ==
               request.RetiredOfferGeneration &&
           string.Equals(
               NormalizeReason(evaluation.RetryReason),
               NormalizeReason(request.RetryReason),
               StringComparison.Ordinal) &&
           string.Equals(
               NormalizeReason(evaluation.RecoveryReason),
               NormalizeReason(request.RecoveryReason),
               StringComparison.Ordinal) &&
           evaluation.RegularV4LivenessGeneration ==
               request.RegularV4LivenessGeneration &&
           evaluation.BridgeDecisionGeneration ==
               request.BridgeDecisionGeneration &&
           evaluation.FallbackPairedTailYieldEvidence ==
               request.FallbackPairedTailYieldEvidence &&
           evaluation.ListenerRearmRequirement ==
               request.ListenerRearmRequirement;

    internal static bool TryGetPendingRuntimeUnlockRetryDispatchAttempt(
        FileTransferCoordinatorState state,
        out RuntimeUnlockRetryLifecycleAttemptSnapshot attempt)
    {
        if (GetRuntimeUnlockRetryLifecycle(state).PendingAttempt is { } pending)
        {
            attempt = pending;
            return true;
        }

        attempt = default;
        return false;
    }

    internal static bool TryGetInstalledRuntimeUnlockRetryDispatchAttempt(
        FileTransferCoordinatorState state,
        out RuntimeUnlockRetryLifecycleAttemptSnapshot attempt)
    {
        if (GetRuntimeUnlockRetryLifecycle(state).InstalledAttempt is
            { IsInstalled: true } installed)
        {
            attempt = installed;
            return true;
        }

        attempt = default;
        return false;
    }

    internal static bool TryGetRetainedRuntimeUnlockRetryDispatchPredecessor(
        FileTransferCoordinatorState state,
        out RuntimeUnlockRetryLifecycleAttemptSnapshot attempt)
    {
        if (GetRuntimeUnlockRetryLifecycle(state).RetainedPredecessor is
            { } predecessor)
        {
            attempt = predecessor;
            return true;
        }

        attempt = default;
        return false;
    }

    internal static bool TryGetCurrentRuntimeUnlockRetryDispatchInstallReceipt(
        FileTransferCoordinatorState state,
        out RuntimeUnlockRetryDispatchInstallReceipt receipt)
    {
        var lifecycle = GetRuntimeUnlockRetryLifecycle(state);
        if (state.IsTerminal ||
            lifecycle.PendingScopeInvalidationCommand is not null)
        {
            receipt = default;
            return false;
        }

        RuntimeUnlockRetryLifecycleAttemptSnapshot? effectiveAttempt =
            lifecycle.InstalledAttempt is
            {
                State:
                    RuntimeUnlockRetryLifecycleAttemptState.Installed or
                    RuntimeUnlockRetryLifecycleAttemptState.AliasBound or
                    RuntimeUnlockRetryLifecycleAttemptState.SendInFlight,
                InstallReceipt: { IsDefault: false },
            } installed
                ? installed
                : lifecycle.PendingAttempt is null &&
                  lifecycle.InstalledAttempt is null &&
                  lifecycle.RetainedPredecessor is null &&
                  lifecycle.BoundedClosure is null &&
                  lifecycle.LastTerminalAttempt is
                  {
                      State:
                          RuntimeUnlockRetryLifecycleAttemptState.Settled,
                      Settlement.Outcome:
                          RuntimeUnlockRetryDispatchSettlementOutcome
                              .DispatchObserved,
                      InstallReceipt: { IsDefault: false },
                  } dispatchObserved
                    ? dispatchObserved
                    : null;
        var coveredByAcceptedScopeInvalidation =
            effectiveAttempt is { } invalidatedAttempt &&
            lifecycle.LastScopeInvalidationReceipt is
            {
                Accepted: true,
                Command: { } invalidationCommand,
            } &&
            (
                invalidationCommand.PendingAttempt == invalidatedAttempt ||
                invalidationCommand.InstalledAttempt == invalidatedAttempt ||
                invalidationCommand.RetainedPredecessor == invalidatedAttempt
            );
        if (effectiveAttempt is not { } attempt ||
            attempt.InstallReceipt is not { IsDefault: false } exactReceipt ||
            coveredByAcceptedScopeInvalidation ||
            !RuntimeUnlockRetryDispatchAuthorityMatchesCurrentScope(
                state,
                attempt.Authority,
                out _))
        {
            receipt = default;
            return false;
        }

        receipt = exactReceipt;
        return true;

    }

    internal static bool IsRuntimeUnlockRetryDispatchAuthorityCurrent(
        FileTransferCoordinatorState state,
        RuntimeUnlockRetryDispatchAuthority authority,
        bool requireInstalled,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        if (authority.IsDefault || state.IsTerminal)
        {
            rejectionReason = state.IsTerminal
                ? "terminal_state"
                : "retry_dispatch_authority_missing";
            return false;
        }

        var lifecycle = GetRuntimeUnlockRetryLifecycle(state);
        if (lifecycle.PendingScopeInvalidationCommand is not null)
        {
            rejectionReason =
                "retry_dispatch_scope_invalidation_pending";
            return false;
        }

        var current = requireInstalled
            ? lifecycle.CurrentInstalledAuthority
            : lifecycle.CurrentAuthorizedAuthority;
        if (current is not { } currentAuthority || currentAuthority != authority)
        {
            rejectionReason = requireInstalled
                ? "retry_dispatch_installed_authority_not_current"
                : "retry_dispatch_authority_not_current";
            return false;
        }

        return RuntimeUnlockRetryDispatchAuthorityMatchesCurrentScope(
            state,
            authority,
            out rejectionReason);
    }

    private static bool IsRuntimeUnlockRetryLifecycleAttemptCurrent(
        FileTransferCoordinatorState state,
        RuntimeUnlockRetryLifecycleAttemptSnapshot attempt,
        out string rejectionReason,
        bool allowTerminalState = false)
    {
        if (state.IsTerminal && !allowTerminalState)
        {
            rejectionReason = "terminal_state";
            return false;
        }

        return RuntimeUnlockRetryDispatchAuthorityMatchesCurrentScope(
            state,
            attempt.Authority,
            out rejectionReason,
            allowTerminalState);
    }

    internal static RuntimeUnlockRetryLifecycleTransitionDecision
        ObserveRuntimeUnlockRetryDispatchStage(
            FileTransferCoordinatorState state,
            RuntimeUnlockRetryDispatchDecisionStageResult stageResult,
            long observedUtcMs)
    {
        RuntimeUnlockRetryLifecycleTransitionDecision Decision(
            RuntimeUnlockRetryLifecycleTransitionStatus status,
            string reason,
            FileTransferCoordinatorState? nextState = null)
            => new(status, reason)
            {
                State = nextState ?? state,
            };

        if (state.IsTerminal)
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "terminal_state");
        }

        if (!stageResult.Accepted ||
            stageResult.Decision.Contract is not { } contract ||
            stageResult.Decision.Request.IsDefault)
        {
            var rejectedLifecycle = GetRuntimeUnlockRetryLifecycle(state);
            var rejectedAttempt =
                stageResult.Decision.Contract is { } rejectedContract &&
                rejectedLifecycle.PendingAttempt is { } exactPending &&
                stageResult.Decision.Request == exactPending.Request &&
                exactPending.Authority == rejectedContract.Authority
                    ? exactPending
                    : (RuntimeUnlockRetryLifecycleAttemptSnapshot?)null;
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_stage_rejected",
                InvalidateRuntimeUnlockRetryReleaseReservationsForRetryAttempts(
                    state,
                    rejectedAttempt,
                    secondaryAttempt: null,
                    reason: "retry_dispatch_stage_rejected"));
        }

        var lifecycle = GetRuntimeUnlockRetryLifecycle(state);
        if (lifecycle.PendingScopeInvalidationCommand is not null)
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_scope_invalidation_pending");
        }

        if (lifecycle.InstalledAttempt is { } installed &&
            installed.Authority == contract.Authority &&
            installed.InstallReceipt is { IsDefault: false } &&
            stageResult.Status == RuntimeUnlockRetryDispatchDecisionStageStatus.Idempotent &&
            stageResult.StageDisposition ==
                RuntimeUnlockRetryDispatchStageDisposition.AlreadyInstalled)
        {
            if (!IsRuntimeUnlockRetryLifecycleAttemptCurrent(
                    state,
                    installed,
                    out var installedCurrentnessReason))
            {
                return Decision(
                    RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                    installedCurrentnessReason);
            }

            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Idempotent,
                "retry_dispatch_stage_already_installed");
        }

        if (lifecycle.PendingAttempt is not { } pending ||
            pending.Authority != contract.Authority ||
            pending.Request != stageResult.Decision.Request)
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_stage_authority_not_pending");
        }

        if (!IsRuntimeUnlockRetryLifecycleAttemptCurrent(
                state,
                pending,
                out var pendingCurrentnessReason))
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                pendingCurrentnessReason);
        }

        if (pending.State == RuntimeUnlockRetryLifecycleAttemptState.Staged)
        {
            if (pending.StageExpiresUtcMs > 0 &&
                observedUtcMs >= pending.StageExpiresUtcMs)
            {
                return EvaluateRuntimeUnlockRetryDispatchStageExpiry(
                    state,
                    pending.Authority.AuthorityId,
                    observedUtcMs);
            }

            var exactDuplicateStatus =
                pending.StageStatus == stageResult.Status ||
                pending.StageStatus ==
                    RuntimeUnlockRetryDispatchDecisionStageStatus.Staged &&
                stageResult.Status ==
                    RuntimeUnlockRetryDispatchDecisionStageStatus.Idempotent;
            if (exactDuplicateStatus &&
                pending.StageDisposition == stageResult.StageDisposition &&
                pending.StagedUtcMs == stageResult.StagedUtcMs)
            {
                return Decision(
                    RuntimeUnlockRetryLifecycleTransitionStatus.Idempotent,
                    "retry_dispatch_stage_duplicate");
            }

            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_stage_tuple_mismatch");
        }

        var deadlineMilliseconds =
            GetRuntimeUnlockRetryDispatchDeadlineMilliseconds();
        if (observedUtcMs <= 0 ||
            stageResult.StagedUtcMs <= 0 ||
            stageResult.StagedUtcMs > long.MaxValue - deadlineMilliseconds ||
            observedUtcMs < stageResult.StagedUtcMs ||
            observedUtcMs >
                stageResult.StagedUtcMs + deadlineMilliseconds)
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_stage_deadline_invalid");
        }

        var staged = pending with
        {
            State = RuntimeUnlockRetryLifecycleAttemptState.Staged,
            StageStatus = stageResult.Status,
            StageDisposition = stageResult.StageDisposition,
            StagedUtcMs = stageResult.StagedUtcMs,
            StageExpiresUtcMs =
                stageResult.StagedUtcMs + deadlineMilliseconds,
        };
        var nextLifecycle = lifecycle with
        {
            PendingAttempt = staged,
        };
        return Decision(
            RuntimeUnlockRetryLifecycleTransitionStatus.Accepted,
            "retry_dispatch_stage_recorded",
            ApplyRuntimeUnlockRetryLifecycle(state, nextLifecycle));
    }

    internal static RuntimeUnlockRetryLifecycleInstallReceiptDecision
        IssueRuntimeUnlockRetryDispatchInstallReceipt(
            FileTransferCoordinatorState state,
            RuntimeUnlockRetryDispatchDecision stagedDecision,
            RuntimeUnlockRetryDispatchAuthorityRequest expectedRequest,
            long observedUtcMs)
    {
        RuntimeUnlockRetryLifecycleInstallReceiptDecision Decision(
            RuntimeUnlockRetryLifecycleTransitionStatus status,
            string reason,
            RuntimeUnlockRetryDispatchInstallReceipt? receipt = null,
            FileTransferCoordinatorState? nextState = null)
            => new(status, reason, receipt)
            {
                State = nextState ?? state,
            };

        if (state.IsTerminal)
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "terminal_state");
        }

        var lifecycle = GetRuntimeUnlockRetryLifecycle(state);
        if (lifecycle.PendingScopeInvalidationCommand is not null)
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_scope_invalidation_pending");
        }

        if (lifecycle.InstalledAttempt is { } installed &&
            installed.Authority == stagedDecision.Contract?.Authority &&
            installed.InstallReceipt is { IsDefault: false } installedReceipt)
        {
            if (!IsRuntimeUnlockRetryLifecycleAttemptCurrent(
                    state,
                    installed,
                    out var installedCurrentnessReason))
            {
                return Decision(
                    RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                    installedCurrentnessReason);
            }

            return installed.Request == expectedRequest &&
                   stagedDecision.Request == expectedRequest
                ? Decision(
                    RuntimeUnlockRetryLifecycleTransitionStatus.Idempotent,
                    "retry_dispatch_install_receipt_already_committed",
                    installedReceipt)
                : Decision(
                    RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                    "retry_dispatch_install_receipt_current_mismatch");
        }

        if (lifecycle.PendingAttempt is not
            {
                State: RuntimeUnlockRetryLifecycleAttemptState.Staged,
            } pending ||
            pending.Authority != stagedDecision.Contract?.Authority)
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_install_receipt_stage_not_current");
        }

        if (!IsRuntimeUnlockRetryLifecycleAttemptCurrent(
                state,
                pending,
                out var pendingCurrentnessReason))
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                pendingCurrentnessReason);
        }

        if (pending.StageExpiresUtcMs <= 0 ||
            observedUtcMs <= 0 ||
            observedUtcMs < pending.StagedUtcMs ||
            observedUtcMs >= pending.StageExpiresUtcMs)
        {
            if (observedUtcMs < pending.StagedUtcMs)
            {
                return Decision(
                    RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                    "retry_dispatch_install_receipt_observation_invalid");
            }

            var expiry = EvaluateRuntimeUnlockRetryDispatchStageExpiry(
                state,
                pending.Authority.AuthorityId,
                observedUtcMs);
            return Decision(
                expiry.Status,
                expiry.Reason,
                nextState: expiry.State);
        }

        if (pending.InstallReceipt is { IsDefault: false } issuedReceipt)
        {
            return issuedReceipt.Authority == pending.Authority &&
                   issuedReceipt.Request == expectedRequest &&
                   issuedReceipt.InstallGeneration ==
                       lifecycle.LastInstallGeneration
                ? Decision(
                    RuntimeUnlockRetryLifecycleTransitionStatus.Idempotent,
                    "retry_dispatch_install_receipt_duplicate",
                    issuedReceipt)
                : Decision(
                    RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                    "retry_dispatch_install_receipt_issued_mismatch");
        }

        if (!TryIssueRuntimeUnlockRetryDispatchInstallReceipt(
                state,
                stagedDecision,
                expectedRequest,
                out var receipt,
                out var mismatchField))
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                $"retry_dispatch_install_receipt_{NormalizeReason(mismatchField)}");
        }

        var dispatchDeadlineMilliseconds =
            GetRuntimeUnlockRetryDispatchDeadlineMilliseconds();
        if (observedUtcMs > long.MaxValue - dispatchDeadlineMilliseconds)
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_install_receipt_deadline_overflow");
        }

        receipt = receipt with
        {
            DispatchDeadlineUtcMs =
                observedUtcMs + dispatchDeadlineMilliseconds,
        };
        var issued = pending with
        {
            InstallReceipt = receipt,
            DispatchDeadlineUtcMs = receipt.DispatchDeadlineUtcMs,
        };
        var nextLifecycle = lifecycle with
        {
            PendingAttempt = issued,
            LastInstallGeneration = receipt.InstallGeneration,
        };
        return Decision(
            RuntimeUnlockRetryLifecycleTransitionStatus.Accepted,
            "retry_dispatch_install_receipt_issued",
            receipt,
            ApplyRuntimeUnlockRetryLifecycle(state, nextLifecycle));
    }

    internal static RuntimeUnlockRetryLifecycleTransitionDecision
        CommitRuntimeUnlockRetryDispatchInstall(
            FileTransferCoordinatorState state,
            RuntimeUnlockRetryDispatchInstallResult installResult,
            long observedUtcMs)
    {
        RuntimeUnlockRetryLifecycleTransitionDecision Decision(
            RuntimeUnlockRetryLifecycleTransitionStatus status,
            string reason,
            FileTransferCoordinatorState? nextState = null,
            RuntimeUnlockRetryDispatchSettlementDecision?
                predecessorSettlementDecision = null)
            => new(status, reason)
            {
                State = nextState ?? state,
                PredecessorSettlementDecision =
                    predecessorSettlementDecision,
            };

        if (state.IsTerminal)
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "terminal_state");
        }

        var lifecycle = GetRuntimeUnlockRetryLifecycle(state);
        if (!installResult.Accepted ||
            installResult.Receipt.IsDefault ||
            observedUtcMs <= 0 ||
            installResult.Receipt.DispatchDeadlineUtcMs <= 0)
        {
            var rejectedAttempt =
                !installResult.Receipt.IsDefault &&
                lifecycle.PendingAttempt is { } exactPending &&
                exactPending.InstallReceipt is { } issuedReceipt &&
                exactPending.Request ==
                    installResult.Receipt.Request &&
                exactPending.Authority ==
                    installResult.Receipt.Authority &&
                issuedReceipt == installResult.Receipt
                    ? exactPending
                    : (RuntimeUnlockRetryLifecycleAttemptSnapshot?)null;
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_install_receipt_rejected",
                InvalidateRuntimeUnlockRetryReleaseReservationsForRetryAttempts(
                    state,
                    rejectedAttempt,
                    secondaryAttempt: null,
                    reason: "retry_dispatch_install_receipt_rejected"));
        }

        var receipt = installResult.Receipt;
        if (lifecycle.PendingScopeInvalidationCommand is not null)
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_scope_invalidation_pending");
        }

        if (!RuntimeUnlockRetryDispatchAuthorityMatchesCurrentScope(
                state,
                receipt.Authority,
                out var receiptCurrentnessReason))
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                receiptCurrentnessReason);
        }

        if (receipt.Authority.ReleaseKind ==
                RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield &&
            receipt.Authority.PredecessorAuthorityId is null)
        {
            var originalFallbackTailYield =
                state.PersistentState.FallbackTailYield;
            var currentState =
                InvalidateFallbackTailYieldForDataSessionReplacement(state);
            if (!ReferenceEquals(
                    originalFallbackTailYield,
                    currentState.PersistentState.FallbackTailYield))
            {
                return Decision(
                    RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                    "data_session_replaced",
                    currentState);
            }
        }

        if (lifecycle.InstalledAttempt is { } installed &&
            installed.Authority == receipt.Authority &&
            installed.InstallReceipt == receipt)
        {
            if (receipt.Authority.PredecessorAuthorityId is null &&
                receipt.Authority.ReleaseKind ==
                    RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield &&
                !IsFallbackTailPairedReleaseConsumedByInstall(
                    state,
                    receipt))
            {
                return Decision(
                    RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                    "fallback_tail_paired_release_consumption_missing");
            }

            RuntimeUnlockRetryDispatchSettlementDecision?
                duplicatePredecessorDecision = null;
            if (lifecycle.RetainedPredecessor is
                {
                    Settlement:
                    {
                        Outcome:
                            RuntimeUnlockRetryDispatchSettlementOutcome
                                .Superseded,
                    } retainedSettlement,
                } pendingRetainedPredecessor &&
                pendingRetainedPredecessor.SuccessorAuthorityId ==
                    installed.Authority.AuthorityId)
            {
                duplicatePredecessorDecision =
                    new RuntimeUnlockRetryDispatchSettlementDecision(
                        RuntimeUnlockRetryDispatchSettlementDecisionStatus
                            .Idempotent,
                        "retry_dispatch_settlement_duplicate",
                        retainedSettlement)
                    {
                        State = state,
                    };
            }
            else if (receipt.Authority.PredecessorAuthorityId is
                     { IsDefault: false } duplicatePredecessorAuthorityId &&
                receipt.Request.RequestActivePredecessorSupersession &&
                lifecycle.LastSettlement is
                {
                    Outcome:
                        RuntimeUnlockRetryDispatchSettlementOutcome.Superseded,
                } duplicatePredecessorSettlement &&
                duplicatePredecessorSettlement.Authority.AuthorityId ==
                    duplicatePredecessorAuthorityId &&
                lifecycle.LastTerminalAttempt is { } terminalPredecessor &&
                terminalPredecessor.Authority ==
                    duplicatePredecessorSettlement.Authority &&
                terminalPredecessor.SuccessorAuthorityId ==
                    receipt.Authority.AuthorityId)
            {
                duplicatePredecessorDecision =
                    new RuntimeUnlockRetryDispatchSettlementDecision(
                        RuntimeUnlockRetryDispatchSettlementDecisionStatus.Idempotent,
                        "retry_dispatch_settlement_duplicate",
                        duplicatePredecessorSettlement)
                    {
                        State = state,
                    };
            }

            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Idempotent,
                "retry_dispatch_install_duplicate",
                predecessorSettlementDecision:
                    duplicatePredecessorDecision);
        }

        var receiptWasIssued =
            lifecycle.PendingAttempt is { } issuedPending &&
            issuedPending.InstallReceipt == receipt &&
            lifecycle.LastInstallGeneration == receipt.InstallGeneration;
        if (lifecycle.PendingAttempt is
            {
                State: RuntimeUnlockRetryLifecycleAttemptState.Staged,
            } expirablePending &&
            expirablePending.Authority == receipt.Authority &&
            expirablePending.StageExpiresUtcMs > 0 &&
            observedUtcMs >= expirablePending.StageExpiresUtcMs)
        {
            var expiry = EvaluateRuntimeUnlockRetryDispatchStageExpiry(
                state,
                receipt.Authority.AuthorityId,
                observedUtcMs);
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Expired,
                expiry.Reason,
                expiry.State);
        }

        if (lifecycle.PendingAttempt is not { } pending ||
            pending.Authority != receipt.Authority ||
            pending.Request != receipt.Request ||
            pending.State != RuntimeUnlockRetryLifecycleAttemptState.Staged ||
            pending.StageStatus != receipt.StageStatus ||
            pending.StageDisposition != receipt.StageDisposition ||
            pending.StagedUtcMs != receipt.StagedUtcMs ||
            pending.StageExpiresUtcMs != receipt.StageExpiresUtcMs ||
            pending.DispatchDeadlineUtcMs != receipt.DispatchDeadlineUtcMs ||
            receipt.StageCleared ||
            !receiptWasIssued ||
            pending.InstallReceipt != receipt ||
            receipt.InstallGeneration != lifecycle.LastInstallGeneration)
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_install_receipt_mismatch");
        }

        RuntimeUnlockRetryLifecycleAttemptSnapshot? retainedPredecessor = null;
        RuntimeUnlockRetryDispatchSettlementReport? predecessorSettlement = null;
        if (receipt.Authority.PredecessorAuthorityId is
            { IsDefault: false } predecessorAuthorityId)
        {
            if (lifecycle.InstalledAttempt is not { } predecessor ||
                predecessor.Authority.AuthorityId != predecessorAuthorityId)
            {
                return Decision(
                    RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                    "retry_dispatch_install_predecessor_not_current");
            }

            if (receipt.Request.RequestActivePredecessorSupersession)
            {
                if (predecessor.IsTerminal)
                {
                    return Decision(
                        RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                        "retry_dispatch_install_active_predecessor_terminal");
                }

                predecessorSettlement =
                    new RuntimeUnlockRetryDispatchSettlementReport(
                        predecessor.Authority,
                        RuntimeUnlockRetryDispatchSettlementOutcome.Superseded,
                        "coordinator_successor_installed")
                    {
                        OfferGeneration = predecessor.OfferGeneration,
                        OfferNonce = predecessor.OfferNonce,
                        DispatchObserved = false,
                        SuccessorAuthorityId =
                            receipt.Authority.AuthorityId,
                    };
                retainedPredecessor = predecessor with
                {
                    State = RuntimeUnlockRetryLifecycleAttemptState.Settled,
                    Settlement = predecessorSettlement,
                    SuccessorAuthorityId = receipt.Authority.AuthorityId,
                };
            }
            else if (predecessor.Settlement is not
                     {
                         Outcome:
                             RuntimeUnlockRetryDispatchSettlementOutcome
                                 .MissingDispatch,
                     })
            {
                return Decision(
                    RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                    "retry_dispatch_install_predecessor_settlement_missing");
            }
        }
        else if (lifecycle.InstalledAttempt is not null)
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_install_conflicting_authority");
        }

        var repairSettlementRelease = lifecycle.RepairSettlementRelease;
        if (receipt.Authority.ReleaseKind ==
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement)
        {
            if (repairSettlementRelease is not { } release ||
                release.IsDefault ||
                release.Settlement.AuthorityId !=
                    receipt.Authority.ReleaseAuthorityId ||
                release.Settlement.DecisionGeneration !=
                    receipt.Authority.ReleaseGeneration ||
                !string.Equals(
                    release.Settlement.Outcome.ToString(),
                    receipt.Authority.ReleaseOutcome,
                    StringComparison.Ordinal))
            {
                return Decision(
                    RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                    "regular_v4_recovery_settlement_consumption_stale");
            }

            if (receipt.Authority.PredecessorAuthorityId is null)
            {
                if (release.State !=
                    RuntimeUnlockRetryLifecycleRepairSettlementReleaseState
                        .Available)
                {
                    return Decision(
                        RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                        "regular_v4_recovery_settlement_consumption_already_used");
                }

                repairSettlementRelease = release with
                {
                    State =
                        RuntimeUnlockRetryLifecycleRepairSettlementReleaseState
                            .Consumed,
                    ConsumingAuthority = receipt.Authority,
                    ConsumingInstallReceipt = receipt,
                };
            }
            else if (release.State !=
                         RuntimeUnlockRetryLifecycleRepairSettlementReleaseState
                             .Consumed ||
                     release.ConsumingAuthority is not { IsDefault: false } ||
                     release.ConsumingInstallReceipt is not
                         { IsDefault: false })
            {
                return Decision(
                    RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                    "retry_dispatch_predecessor_recovery_handoff_stale");
            }
        }

        var installedAttempt = pending with
        {
            State = RuntimeUnlockRetryLifecycleAttemptState.Installed,
            InstallReceipt = receipt,
            DispatchDeadlineUtcMs = receipt.DispatchDeadlineUtcMs,
        };
        var nextLifecycle = lifecycle with
        {
            PendingAttempt = null,
            InstalledAttempt = installedAttempt,
            RetainedPredecessor = retainedPredecessor,
            LastTerminalAttempt =
                retainedPredecessor is null &&
                receipt.Authority.PredecessorAuthorityId is not null
                     ? lifecycle.InstalledAttempt
                     : lifecycle.LastTerminalAttempt,
            LastSettlement = lifecycle.LastSettlement,
            AttemptHistory = lifecycle.AttemptHistory,
            LastInstallGeneration = receipt.InstallGeneration,
            BoundedClosure = null,
            RepairSettlementRelease = repairSettlementRelease,
        };
        var committedState = ApplyRuntimeUnlockRetryLifecycle(
            state,
            nextLifecycle);

        if (receipt.Authority.PredecessorAuthorityId is null &&
            receipt.Authority.ReleaseKind ==
                RuntimeUnlockRetryReleaseKind.ReceiveRecoveryLeaseClosure &&
            committedState.PersistentState.FileTransferReceiveRecovery
                .CurrentOperation is not null)
        {
            var consumption =
                ConsumeReceiveRecoveryClosure(committedState, receipt);
            if (!consumption.Accepted)
            {
                return Decision(
                    RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                    consumption.Reason);
            }

            committedState = consumption.State;
        }

        if (receipt.Authority.PredecessorAuthorityId is null &&
            receipt.Authority.ReleaseKind ==
                RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield)
        {
            if (committedState.PersistentState.FallbackTailYield
                    .CurrentReservation is not { } reservation)
            {
                return Decision(
                    RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                    "fallback_tail_paired_release_reservation_missing");
            }

            var consumption = ConsumeFallbackTailPairedRelease(
                committedState,
                reservation,
                receipt,
                installResult.Status);
            if (!consumption.Accepted)
            {
                return Decision(
                    RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                    consumption.Reason);
            }

            committedState = consumption.State;
        }

        RuntimeUnlockRetryDispatchSettlementDecision?
            predecessorSettlementDecision = null;
        if (predecessorSettlement is { } exactPredecessorSettlement)
        {
            predecessorSettlementDecision =
                new RuntimeUnlockRetryDispatchSettlementDecision(
                    RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted,
                    "retry_dispatch_settlement_superseded_predecessor",
                    exactPredecessorSettlement)
                {
                    State = committedState,
                };
        }
        return Decision(
            RuntimeUnlockRetryLifecycleTransitionStatus.Accepted,
            "retry_dispatch_install_committed",
            committedState,
            predecessorSettlementDecision);
    }

    internal static RuntimeUnlockRetryLifecycleTransitionDecision
        EvaluateRuntimeUnlockRetryDispatchStageExpiry(
            FileTransferCoordinatorState state,
            FileTransferAuthorityId authorityId,
            long observedUtcMs)
    {
        RuntimeUnlockRetryLifecycleTransitionDecision Decision(
            RuntimeUnlockRetryLifecycleTransitionStatus status,
            string reason,
            FileTransferCoordinatorState? nextState = null)
            => new(status, reason)
            {
                State = nextState ?? state,
            };

        var lifecycle = GetRuntimeUnlockRetryLifecycle(state);
        if (lifecycle.PendingScopeInvalidationCommand is not null)
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_scope_invalidation_pending");
        }

        if (authorityId.IsDefault ||
            lifecycle.PendingAttempt is not { } pending ||
            pending.Authority.AuthorityId != authorityId)
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_stage_not_current");
        }

        if (pending.State != RuntimeUnlockRetryLifecycleAttemptState.Staged ||
            pending.StageExpiresUtcMs <= 0)
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_stage_not_expirable");
        }

        if (!IsRuntimeUnlockRetryLifecycleAttemptCurrent(
                state,
                pending,
                out var pendingCurrentnessReason))
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                pendingCurrentnessReason);
        }

        if (observedUtcMs < pending.StageExpiresUtcMs)
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Idempotent,
                "retry_dispatch_stage_deadline_pending");
        }

        var expired = pending with
        {
            State = RuntimeUnlockRetryLifecycleAttemptState.Invalidated,
        };
        var nextLifecycle = lifecycle with
        {
            PendingAttempt = null,
            LastTerminalAttempt = expired,
            AttemptHistory =
                AppendRuntimeUnlockRetryLifecycleAttemptHistory(
                    lifecycle.AttemptHistory,
                    expired),
        };
        return Decision(
            RuntimeUnlockRetryLifecycleTransitionStatus.Expired,
            "retry_dispatch_stage_expired",
            InvalidateRuntimeUnlockRetryReleaseReservationsForRetryAttempts(
                ApplyRuntimeUnlockRetryLifecycle(state, nextLifecycle),
                expired,
                secondaryAttempt: null,
                reason: "retry_dispatch_stage_expired"));
    }

    internal static RuntimeUnlockRetryLifecycleScopeInvalidationDecision
        PrepareRuntimeUnlockRetryDispatchStageExpiry(
            FileTransferCoordinatorState state,
            FileTransferAuthorityId authorityId,
            long observedUtcMs)
    {
        RuntimeUnlockRetryLifecycleScopeInvalidationDecision Decision(
            RuntimeUnlockRetryLifecycleTransitionStatus status,
            string reason,
            RuntimeUnlockRetryDispatchScopeInvalidationCommand? command = null,
            FileTransferCoordinatorState? nextState = null)
            => new(status, reason, command)
            {
                State = nextState ?? state,
            };

        var lifecycle = GetRuntimeUnlockRetryLifecycle(state);
        if (lifecycle.PendingScopeInvalidationCommand is
            {
                Kind:
                    RuntimeUnlockRetryDispatchScopeInvalidationKind
                        .PendingStageExpiry,
            } pendingCommand)
        {
            return pendingCommand.PendingAttempt?.Authority.AuthorityId ==
                   authorityId
                ? Decision(
                    RuntimeUnlockRetryLifecycleTransitionStatus.Idempotent,
                    "retry_dispatch_stage_expiry_already_prepared",
                    pendingCommand)
                : Decision(
                    RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                    "retry_dispatch_scope_invalidation_pending_mismatch");
        }

        var expiry =
            EvaluateRuntimeUnlockRetryDispatchStageExpiry(
                state,
                authorityId,
                observedUtcMs);
        if (expiry.Status != RuntimeUnlockRetryLifecycleTransitionStatus.Expired)
        {
            return Decision(expiry.Status, expiry.Reason);
        }

        if (lifecycle.PendingAttempt is not { } pendingAttempt ||
            lifecycle.LastScopeInvalidationGeneration == long.MaxValue)
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                lifecycle.PendingAttempt is null
                    ? "retry_dispatch_stage_not_current"
                    : "retry_dispatch_scope_invalidation_generation_exhausted");
        }

        const string reason = "retry_dispatch_stage_expired";
        var generation = lifecycle.LastScopeInvalidationGeneration + 1;
        var command =
            new RuntimeUnlockRetryDispatchScopeInvalidationCommand(
                pendingAttempt,
                lifecycle.InstalledAttempt,
                lifecycle.RetainedPredecessor,
                lifecycle.BoundedClosure,
                generation,
                reason,
                ComputeRuntimeUnlockRetryDispatchScopeInvalidationCommandId(
                    pendingAttempt,
                    lifecycle.InstalledAttempt,
                    lifecycle.RetainedPredecessor,
                    lifecycle.BoundedClosure,
                    generation,
                    reason,
                    RuntimeUnlockRetryDispatchScopeInvalidationKind
                        .PendingStageExpiry),
                RuntimeUnlockRetryDispatchScopeInvalidationKind
                    .PendingStageExpiry);
        var preparedLifecycle = lifecycle with
        {
            PendingScopeInvalidationCommand = command,
            LastScopeInvalidationGeneration = generation,
        };
        return Decision(
            RuntimeUnlockRetryLifecycleTransitionStatus.Accepted,
            "retry_dispatch_stage_expiry_prepared",
            command,
            ApplyRuntimeUnlockRetryLifecycle(state, preparedLifecycle));
    }

    internal static RuntimeUnlockRetryLifecycleTransitionDecision
        InvalidateRuntimeUnlockRetryDispatchPendingAttempt(
            FileTransferCoordinatorState state,
            FileTransferAuthorityId authorityId,
            string reason)
    {
        RuntimeUnlockRetryLifecycleTransitionDecision Decision(
            RuntimeUnlockRetryLifecycleTransitionStatus status,
            string decisionReason,
            FileTransferCoordinatorState? nextState = null)
            => new(status, decisionReason)
            {
                State = nextState ?? state,
            };

        var normalizedReason = NormalizeReason(reason);
        var lifecycle = GetRuntimeUnlockRetryLifecycle(state);
        if (lifecycle.PendingScopeInvalidationCommand is not null)
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_scope_invalidation_pending");
        }

        if (authorityId.IsDefault ||
            string.IsNullOrWhiteSpace(normalizedReason))
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_pending_invalidation_invalid");
        }

        if (lifecycle.PendingAttempt is not { } pending)
        {
            if (lifecycle.LastTerminalAttempt is
                {
                    State: RuntimeUnlockRetryLifecycleAttemptState.Invalidated,
                } terminal &&
                terminal.Authority.AuthorityId == authorityId)
            {
                return Decision(
                    RuntimeUnlockRetryLifecycleTransitionStatus.Idempotent,
                    "retry_dispatch_pending_already_invalidated");
            }

            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_pending_not_current");
        }

        if (pending.Authority.AuthorityId != authorityId)
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_pending_authority_mismatch");
        }

        var invalidated = pending with
        {
            State = RuntimeUnlockRetryLifecycleAttemptState.Invalidated,
        };
        return Decision(
            RuntimeUnlockRetryLifecycleTransitionStatus.Accepted,
            normalizedReason,
            InvalidateRuntimeUnlockRetryReleaseReservationsForRetryAttempts(
                ApplyRuntimeUnlockRetryLifecycle(
                    state,
                    lifecycle with
                    {
                        PendingAttempt = null,
                        LastTerminalAttempt = invalidated,
                        AttemptHistory =
                            AppendRuntimeUnlockRetryLifecycleAttemptHistory(
                                lifecycle.AttemptHistory,
                                invalidated),
                    }),
                invalidated,
                secondaryAttempt: null,
                reason: normalizedReason));
    }

    internal static RuntimeUnlockRetryLifecycleScopeInvalidationDecision
        PrepareRuntimeUnlockRetryLifecycleScopeInvalidation(
            FileTransferCoordinatorState state,
            string reason)
    {
        RuntimeUnlockRetryLifecycleScopeInvalidationDecision Decision(
            RuntimeUnlockRetryLifecycleTransitionStatus status,
            string decisionReason,
            RuntimeUnlockRetryDispatchScopeInvalidationCommand? command = null,
            FileTransferCoordinatorState? nextState = null)
            => new(status, decisionReason, command)
            {
                State = nextState ?? state,
            };

        var normalizedReason = NormalizeReason(reason);
        if (string.IsNullOrWhiteSpace(normalizedReason))
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_scope_invalidation_reason_missing");
        }

        var lifecycle = GetRuntimeUnlockRetryLifecycle(state);
        var installedForScopeInvalidation =
            GetRuntimeUnlockRetryLifecycleAttemptForScopeInvalidation(
                lifecycle);
        if (lifecycle.PendingAttempt is null &&
            installedForScopeInvalidation is null &&
            lifecycle.RetainedPredecessor is null &&
            lifecycle.BoundedClosure is null)
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Idempotent,
                "retry_dispatch_scope_already_invalidated");
        }

        if (lifecycle.PendingScopeInvalidationCommand is { } pending)
        {
            var exact =
                pending.Kind ==
                    RuntimeUnlockRetryDispatchScopeInvalidationKind.FullScope &&
                pending.PendingAttempt == lifecycle.PendingAttempt &&
                pending.InstalledAttempt ==
                    installedForScopeInvalidation &&
                pending.RetainedPredecessor == lifecycle.RetainedPredecessor &&
                pending.BoundedClosure == lifecycle.BoundedClosure;
            return exact
                ? Decision(
                    RuntimeUnlockRetryLifecycleTransitionStatus.Idempotent,
                    "retry_dispatch_scope_invalidation_already_prepared",
                    pending)
                : Decision(
                    RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                    "retry_dispatch_scope_invalidation_pending_mismatch");
        }

        if (lifecycle.LastScopeInvalidationGeneration == long.MaxValue)
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_scope_invalidation_generation_exhausted");
        }

        var command =
            new RuntimeUnlockRetryDispatchScopeInvalidationCommand(
                lifecycle.PendingAttempt,
                installedForScopeInvalidation,
                lifecycle.RetainedPredecessor,
                lifecycle.BoundedClosure,
                lifecycle.LastScopeInvalidationGeneration + 1,
                normalizedReason,
                ComputeRuntimeUnlockRetryDispatchScopeInvalidationCommandId(
                    lifecycle.PendingAttempt,
                    installedForScopeInvalidation,
                    lifecycle.RetainedPredecessor,
                    lifecycle.BoundedClosure,
                    lifecycle.LastScopeInvalidationGeneration + 1,
                    normalizedReason,
                    RuntimeUnlockRetryDispatchScopeInvalidationKind
                        .FullScope));
        var preparedLifecycle = lifecycle with
        {
            PendingScopeInvalidationCommand = command,
            LastScopeInvalidationGeneration =
                command.InvalidationGeneration,
        };
        return Decision(
            RuntimeUnlockRetryLifecycleTransitionStatus.Accepted,
            "retry_dispatch_scope_invalidation_prepared",
            command,
            ApplyRuntimeUnlockRetryLifecycle(state, preparedLifecycle));
    }

    internal static RuntimeUnlockRetryLifecycleScopeInvalidationDecision
        CommitRuntimeUnlockRetryLifecycleScopeInvalidation(
            FileTransferCoordinatorState state,
            RuntimeUnlockRetryDispatchScopeInvalidationReceipt receipt)
    {
        RuntimeUnlockRetryLifecycleScopeInvalidationDecision Decision(
            RuntimeUnlockRetryLifecycleTransitionStatus status,
            string reason,
            RuntimeUnlockRetryDispatchScopeInvalidationCommand? command = null,
            FileTransferCoordinatorState? nextState = null)
            => new(status, reason, command)
            {
                State = nextState ?? state,
            };

        var lifecycle = GetRuntimeUnlockRetryLifecycle(state);
        var installedForScopeInvalidation =
            GetRuntimeUnlockRetryLifecycleAttemptForScopeInvalidation(
                lifecycle);
        if (receipt.Command is null || receipt.Command.IsDefault)
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_scope_invalidation_receipt_missing");
        }

        if (lifecycle.PendingScopeInvalidationCommand is null &&
            receipt.Accepted &&
            lifecycle.LastScopeInvalidationReceipt is { } lastReceipt &&
            lastReceipt.Command == receipt.Command)
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Idempotent,
                "retry_dispatch_scope_invalidation_duplicate",
                receipt.Command);
        }

        if (!receipt.Accepted ||
            lifecycle.PendingScopeInvalidationCommand is not { } pending ||
            pending != receipt.Command ||
            pending.InvalidationGeneration !=
                lifecycle.LastScopeInvalidationGeneration ||
            pending.PendingAttempt != lifecycle.PendingAttempt ||
            pending.InstalledAttempt !=
                installedForScopeInvalidation ||
            pending.RetainedPredecessor != lifecycle.RetainedPredecessor ||
            pending.BoundedClosure != lifecycle.BoundedClosure)
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_scope_invalidation_receipt_mismatch");
        }

        RuntimeUnlockRetryLifecycleSubstate invalidatedLifecycle;
        if (pending.Kind ==
            RuntimeUnlockRetryDispatchScopeInvalidationKind.PendingStageExpiry)
        {
            if (lifecycle.PendingAttempt is not { } stagedAttempt ||
                stagedAttempt.State !=
                    RuntimeUnlockRetryLifecycleAttemptState.Staged)
            {
                return Decision(
                    RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                    "retry_dispatch_stage_expiry_receipt_mismatch");
            }

            var expiredAttempt = stagedAttempt with
            {
                State = RuntimeUnlockRetryLifecycleAttemptState.Invalidated,
            };
            invalidatedLifecycle = lifecycle with
            {
                PendingAttempt = null,
                LastTerminalAttempt = expiredAttempt,
                PendingScopeInvalidationCommand = null,
                LastScopeInvalidationReceipt = receipt,
                LastScopeInvalidationGeneration =
                    pending.InvalidationGeneration,
                AttemptHistory =
                    AppendRuntimeUnlockRetryLifecycleAttemptHistory(
                        lifecycle.AttemptHistory,
                        expiredAttempt),
            };
        }
        else
        {
            invalidatedLifecycle =
                InvalidateRuntimeUnlockRetryLifecycle(lifecycle) with
                {
                    PendingScopeInvalidationCommand = null,
                    LastScopeInvalidationReceipt = receipt,
                    LastScopeInvalidationGeneration =
                        pending.InvalidationGeneration,
                };
        }
        var invalidatedState = ApplyRuntimeUnlockRetryLifecycle(
            state,
            invalidatedLifecycle);
        invalidatedState =
            InvalidateRuntimeUnlockRetryReleaseReservationsForRetryAttempts(
                invalidatedState,
                pending.PendingAttempt,
                pending.InstalledAttempt,
                pending.Reason);
        return Decision(
            RuntimeUnlockRetryLifecycleTransitionStatus.Accepted,
            pending.Kind ==
                RuntimeUnlockRetryDispatchScopeInvalidationKind
                    .PendingStageExpiry
                ? "retry_dispatch_stage_expired"
                : "retry_dispatch_scope_invalidated",
            pending,
            invalidatedState);
    }

    internal static bool
        IsRuntimeUnlockRetryDispatchScopeInvalidationCommandIdentityValid(
            RuntimeUnlockRetryDispatchScopeInvalidationCommand command)
        => !command.IsDefault &&
           command.CommandId ==
           ComputeRuntimeUnlockRetryDispatchScopeInvalidationCommandId(
               command.PendingAttempt,
               command.InstalledAttempt,
               command.RetainedPredecessor,
               command.BoundedClosure,
               command.InvalidationGeneration,
               command.Reason,
               command.Kind);

    private static FileTransferAuthorityId
        ComputeRuntimeUnlockRetryDispatchScopeInvalidationCommandId(
            RuntimeUnlockRetryLifecycleAttemptSnapshot? pendingAttempt,
            RuntimeUnlockRetryLifecycleAttemptSnapshot? installedAttempt,
            RuntimeUnlockRetryLifecycleAttemptSnapshot? retainedPredecessor,
            RuntimeUnlockRetryLifecycleBoundedClosure? boundedClosure,
            long invalidationGeneration,
            string reason,
            RuntimeUnlockRetryDispatchScopeInvalidationKind kind)
    {
        var builder = new StringBuilder();
        AppendRuntimeUnlockRetryDispatchScopeInvalidationIdentityPart(
            builder,
            pendingAttempt?.ToString());
        AppendRuntimeUnlockRetryDispatchScopeInvalidationIdentityPart(
            builder,
            installedAttempt?.ToString());
        AppendRuntimeUnlockRetryDispatchScopeInvalidationIdentityPart(
            builder,
            retainedPredecessor?.ToString());
        AppendRuntimeUnlockRetryDispatchScopeInvalidationIdentityPart(
            builder,
            boundedClosure?.ToString());
        AppendRuntimeUnlockRetryDispatchScopeInvalidationIdentityPart(
            builder,
            invalidationGeneration.ToString(CultureInfo.InvariantCulture));
        AppendRuntimeUnlockRetryDispatchScopeInvalidationIdentityPart(
            builder,
            reason);
        AppendRuntimeUnlockRetryDispatchScopeInvalidationIdentityPart(
            builder,
            kind.ToString());
        var digest = SHA256.HashData(
            Encoding.UTF8.GetBytes(builder.ToString()));
        return new FileTransferAuthorityId(
            $"runtime-unlock-retry-scope-{Convert.ToHexString(digest).ToLowerInvariant()}");
    }

    private static void
        AppendRuntimeUnlockRetryDispatchScopeInvalidationIdentityPart(
            StringBuilder builder,
            string? value)
    {
        value ??= string.Empty;
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(value);
        builder.Append(';');
    }

    internal static RuntimeUnlockRetryLifecycleDeadlineDecision
        EvaluateRuntimeUnlockRetryDispatchDeadline(
            FileTransferCoordinatorState state,
            FileTransferAuthorityId authorityId,
            long observedUtcMs)
    {
        RuntimeUnlockRetryLifecycleDeadlineDecision Decision(
            RuntimeUnlockRetryLifecycleTransitionStatus status,
            string reason,
            RuntimeUnlockRetryDispatchSettlementReport? settlement = null,
            FileTransferCoordinatorState? nextState = null)
            => new(status, reason, settlement)
            {
                State = nextState ?? state,
            };

        var lifecycle = GetRuntimeUnlockRetryLifecycle(state);
        if (lifecycle.PendingScopeInvalidationCommand is not null)
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_scope_invalidation_pending");
        }

        if (authorityId.IsDefault ||
            lifecycle.InstalledAttempt is not { } installed ||
            installed.Authority.AuthorityId != authorityId ||
            !installed.IsInstalled)
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_deadline_authority_not_installed");
        }

        if (!IsRuntimeUnlockRetryLifecycleAttemptCurrent(
                state,
                installed,
                out var installedCurrentnessReason))
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                installedCurrentnessReason);
        }

        if (installed.Settlement is { } existingSettlement)
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Idempotent,
                "retry_dispatch_deadline_already_settled",
                existingSettlement);
        }

        if (installed.DispatchDeadlineUtcMs <= 0)
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_deadline_missing");
        }

        if (observedUtcMs < installed.DispatchDeadlineUtcMs)
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Idempotent,
                "retry_dispatch_deadline_pending");
        }

        if (installed.DispatchObserved)
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Idempotent,
                "retry_dispatch_deadline_dispatch_observed");
        }

        var settlement = new RuntimeUnlockRetryDispatchSettlementReport(
            installed.Authority,
            RuntimeUnlockRetryDispatchSettlementOutcome.MissingDispatch,
            "retry_dispatch_deadline_elapsed")
        {
            OfferGeneration = installed.OfferGeneration,
            OfferNonce = installed.OfferNonce,
            DispatchObserved = false,
        };
        var settlementDecision =
            ObserveRuntimeUnlockRetryDispatchSettlement(state, settlement);
        if (settlementDecision.Status is not
            (RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted or
             RuntimeUnlockRetryDispatchSettlementDecisionStatus.Idempotent))
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                settlementDecision.Reason);
        }

        return Decision(
            settlementDecision.Status ==
                RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted
                ? RuntimeUnlockRetryLifecycleTransitionStatus.Accepted
                : RuntimeUnlockRetryLifecycleTransitionStatus.Idempotent,
            settlementDecision.Reason,
            settlement,
            settlementDecision.State);
    }

    internal static RuntimeUnlockRetryLifecycleTransitionDecision
        ObserveRuntimeUnlockRetryDispatchExecutionState(
            FileTransferCoordinatorState state,
            RuntimeUnlockRetryDispatchInstallReceipt installReceipt,
            RuntimeUnlockRetryDispatchAttemptPhase attemptPhase,
            long offerGeneration,
            string? offerNonce,
            bool dispatchObserved)
    {
        RuntimeUnlockRetryLifecycleTransitionDecision Decision(
            RuntimeUnlockRetryLifecycleTransitionStatus status,
            string reason,
            FileTransferCoordinatorState? nextState = null)
            => new(status, reason)
            {
                State = nextState ?? state,
            };

        var lifecycle = GetRuntimeUnlockRetryLifecycle(state);
        if (lifecycle.PendingScopeInvalidationCommand is not null)
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_scope_invalidation_pending");
        }

        if (installReceipt.IsDefault ||
            installReceipt.DispatchDeadlineUtcMs <= 0 ||
            lifecycle.InstalledAttempt is not { } installed ||
            !installed.IsInstalled ||
            installed.Authority != installReceipt.Authority ||
            installed.InstallReceipt != installReceipt)
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_execution_receipt_not_installed");
        }

        if (!IsRuntimeUnlockRetryLifecycleAttemptCurrent(
                state,
                installed,
                out var installedCurrentnessReason))
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                installedCurrentnessReason);
        }

        if (dispatchObserved ||
            attemptPhase == RuntimeUnlockRetryDispatchAttemptPhase.Settled)
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_execution_transition_invalid");
        }

        if (attemptPhase == RuntimeUnlockRetryDispatchAttemptPhase.Granted)
        {
            return installed.State ==
                       RuntimeUnlockRetryLifecycleAttemptState.Installed &&
                   offerGeneration == 0 &&
                   string.IsNullOrWhiteSpace(offerNonce)
                ? Decision(
                    RuntimeUnlockRetryLifecycleTransitionStatus.Idempotent,
                    "retry_dispatch_execution_duplicate")
                : Decision(
                    RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                    "retry_dispatch_execution_transition_invalid");
        }

        if (offerGeneration <= 0 ||
            string.IsNullOrWhiteSpace(offerNonce) ||
            !string.Equals(offerNonce, offerNonce.Trim(), StringComparison.Ordinal))
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_execution_offer_tuple_invalid");
        }

        RuntimeUnlockRetryLifecycleAttemptState requestedState;
        if (attemptPhase == RuntimeUnlockRetryDispatchAttemptPhase.AliasBound)
        {
            requestedState = RuntimeUnlockRetryLifecycleAttemptState.AliasBound;
            if (installed.State ==
                    RuntimeUnlockRetryLifecycleAttemptState.AliasBound &&
                installed.OfferGeneration == offerGeneration &&
                string.Equals(
                    installed.OfferNonce,
                    offerNonce,
                    StringComparison.Ordinal))
            {
                return Decision(
                    RuntimeUnlockRetryLifecycleTransitionStatus.Idempotent,
                    "retry_dispatch_execution_duplicate");
            }

            if (installed.State !=
                    RuntimeUnlockRetryLifecycleAttemptState.Installed ||
                installed.OfferGeneration != 0 ||
                installed.OfferNonce is not null)
            {
                return Decision(
                    RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                    "retry_dispatch_execution_transition_invalid");
            }
        }
        else if (attemptPhase ==
                 RuntimeUnlockRetryDispatchAttemptPhase.SendInFlight)
        {
            requestedState =
                RuntimeUnlockRetryLifecycleAttemptState.SendInFlight;
            if (installed.State ==
                    RuntimeUnlockRetryLifecycleAttemptState.SendInFlight &&
                installed.OfferGeneration == offerGeneration &&
                string.Equals(
                    installed.OfferNonce,
                    offerNonce,
                    StringComparison.Ordinal))
            {
                return Decision(
                    RuntimeUnlockRetryLifecycleTransitionStatus.Idempotent,
                    "retry_dispatch_execution_duplicate");
            }

            if (installed.State !=
                    RuntimeUnlockRetryLifecycleAttemptState.AliasBound ||
                installed.OfferGeneration != offerGeneration ||
                !string.Equals(
                    installed.OfferNonce,
                    offerNonce,
                    StringComparison.Ordinal))
            {
                return Decision(
                    RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                    "retry_dispatch_execution_transition_invalid");
            }
        }
        else
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_execution_transition_invalid");
        }

        var updated = installed with
        {
            State = requestedState,
            OfferGeneration = offerGeneration,
            OfferNonce = offerNonce,
        };
        return Decision(
            RuntimeUnlockRetryLifecycleTransitionStatus.Accepted,
            "retry_dispatch_execution_recorded",
            ApplyRuntimeUnlockRetryLifecycle(
                state,
                lifecycle with
                {
                    InstalledAttempt = updated,
                }));
    }

    internal static RuntimeUnlockRetryDispatchSuccessorDecision
        PrepareRuntimeUnlockRetryDispatchSuccessor(
            FileTransferCoordinatorState state,
            RuntimeUnlockRetryDispatchAuthority predecessorAuthority,
            long retiredOfferGeneration) =>
        PrepareRuntimeUnlockRetryDispatchSuccessor(
            state,
            new RuntimeUnlockRetryDispatchSuccessorEvaluation(
                predecessorAuthority,
                retiredOfferGeneration));

    internal static RuntimeUnlockRetryDispatchSuccessorDecision
        PrepareRuntimeUnlockRetryDispatchSuccessor(
            FileTransferCoordinatorState state,
            RuntimeUnlockRetryDispatchSuccessorEvaluation evaluation)
    {
        RuntimeUnlockRetryDispatchSuccessorDecision Decision(
            RuntimeUnlockRetryDispatchSuccessorDecisionStatus status,
            string reason,
            RuntimeUnlockRetryDispatchAuthorityRequest? request = null,
            RuntimeUnlockRetryDispatchDecision? authorizationDecision = null,
            FileTransferCoordinatorState? nextState = null)
            => new(
                status,
                reason,
                evaluation,
                request,
                authorizationDecision)
            {
                State = nextState ?? state,
            };

        if (evaluation.IsDefault)
        {
            return Decision(
                RuntimeUnlockRetryDispatchSuccessorDecisionStatus.Rejected,
                "retry_dispatch_successor_evaluation_missing");
        }

        var predecessorAuthorityId =
            evaluation.PredecessorAuthority.AuthorityId;
        var retiredOfferGeneration = evaluation.RetiredOfferGeneration;
        if (state.IsTerminal)
        {
            return Decision(
                RuntimeUnlockRetryDispatchSuccessorDecisionStatus.Rejected,
                "terminal_state");
        }

        var lifecycle = GetRuntimeUnlockRetryLifecycle(state);
        if (lifecycle.PendingScopeInvalidationCommand is not null)
        {
            return Decision(
                RuntimeUnlockRetryDispatchSuccessorDecisionStatus.Rejected,
                "retry_dispatch_scope_invalidation_pending");
        }

        if (predecessorAuthorityId.IsDefault ||
            lifecycle.InstalledAttempt is not { } predecessor ||
            predecessor.Authority.AuthorityId != predecessorAuthorityId ||
            retiredOfferGeneration <= 0)
        {
            return Decision(
                RuntimeUnlockRetryDispatchSuccessorDecisionStatus.Rejected,
                "retry_dispatch_successor_predecessor_not_current");
        }

        var expectedRetiredOfferGeneration =
            predecessor.OfferGeneration > 0
                ? predecessor.OfferGeneration
                : predecessor.Authority.RetiredOfferGeneration;
        if (retiredOfferGeneration != expectedRetiredOfferGeneration)
        {
            return Decision(
                RuntimeUnlockRetryDispatchSuccessorDecisionStatus.Rejected,
                "retry_dispatch_successor_retired_offer_mismatch");
        }

        if (!IsRuntimeUnlockRetryLifecyclePredecessorCurrentForSuccessor(
                state,
                predecessor,
                out var predecessorCurrentnessReason))
        {
            return Decision(
                RuntimeUnlockRetryDispatchSuccessorDecisionStatus.Rejected,
                predecessorCurrentnessReason);
        }

        var activePredecessorSupersession =
            predecessor.Settlement is null;
        if (activePredecessorSupersession)
        {
            if (predecessor.IsTerminal ||
                predecessor.State !=
                    RuntimeUnlockRetryLifecycleAttemptState.AliasBound ||
                predecessor.DispatchObserved ||
                predecessor.Request.ListenerRearmRequirement ==
                    RuntimeUnlockRetryDispatchListenerRearmRequirement.None)
            {
                return Decision(
                    RuntimeUnlockRetryDispatchSuccessorDecisionStatus.Held,
                    "retry_dispatch_successor_predecessor_not_supersedable");
            }
        }
        else if (predecessor.Settlement is not
                 {
                     Outcome:
                         RuntimeUnlockRetryDispatchSettlementOutcome
                             .MissingDispatch,
                 })
        {
            return Decision(
                RuntimeUnlockRetryDispatchSuccessorDecisionStatus.Held,
                "retry_dispatch_successor_missing_dispatch_not_accepted");
        }

        var missingDispatchGeneration =
            predecessor.Authority.MissingDispatchSuccessorGeneration +
            (activePredecessorSupersession ? 0 : 1);
        var request = new RuntimeUnlockRetryDispatchAuthorityRequest(
            predecessor.Authority.SessionId,
            predecessor.Authority.TransferId,
            predecessor.Authority.Direction,
            predecessor.Request.SourcePublicationAuthority,
            predecessor.Authority.RecoveryContractGeneration,
            retiredOfferGeneration,
            predecessor.Authority.DispatchAttemptGeneration + 1,
            predecessor.Authority.RetryReason,
            predecessor.Authority.RecoveryReason,
            predecessor.Request.RegularV4LivenessGeneration,
            predecessor.Request.BridgeDecisionGeneration,
            predecessor.Authority.AuthorityId,
            activePredecessorSupersession,
            missingDispatchGeneration)
        {
            FallbackPairedTailYieldEvidence =
                predecessor.Request.FallbackPairedTailYieldEvidence,
            ListenerRearmRequirement =
                predecessor.Request.ListenerRearmRequirement,
        };

        if (!activePredecessorSupersession &&
            missingDispatchGeneration >
                RuntimeUnlockRetryMissingDispatchSuccessorLimit)
        {
            var closure = CloseRuntimeUnlockRetryDispatchChain(
                state,
                request,
                "retry_dispatch_missing_successor_limit_exhausted");
            return closure.Status is
                RuntimeUnlockRetryLifecycleTransitionStatus.Accepted or
                RuntimeUnlockRetryLifecycleTransitionStatus.Idempotent
                ? Decision(
                    RuntimeUnlockRetryDispatchSuccessorDecisionStatus.ChainClosed,
                    closure.Reason,
                    request,
                    nextState: closure.State)
                : Decision(
                    RuntimeUnlockRetryDispatchSuccessorDecisionStatus.Rejected,
                    closure.Reason);
        }

        if (lifecycle.PendingAttempt is { } existingPending)
        {
            if (existingPending.Request == request)
            {
                var duplicate = AuthorizeRuntimeUnlockRetryDispatchCore(
                    state,
                    request,
                    predecessor.Authority.ReleaseKind,
                    predecessor.Authority.ReleaseAuthorityId,
                    predecessor.Authority.ReleaseGeneration,
                    predecessor.Authority.ReleaseOutcome,
                    allowCoordinatorPreparedSuccessor: true);
                return Decision(
                    RuntimeUnlockRetryDispatchSuccessorDecisionStatus.Idempotent,
                    duplicate.Reason,
                    request,
                    duplicate,
                    duplicate.State);
            }

            return Decision(
                RuntimeUnlockRetryDispatchSuccessorDecisionStatus.Rejected,
                "retry_dispatch_successor_conflict");
        }

        var authorization = AuthorizeRuntimeUnlockRetryDispatchCore(
            state,
            request,
            predecessor.Authority.ReleaseKind,
            predecessor.Authority.ReleaseAuthorityId,
            predecessor.Authority.ReleaseGeneration,
            predecessor.Authority.ReleaseOutcome,
            allowCoordinatorPreparedSuccessor: true);
        var status = authorization.Status switch
        {
            RuntimeUnlockRetryDispatchDecisionStatus.Granted =>
                RuntimeUnlockRetryDispatchSuccessorDecisionStatus.Requested,
            RuntimeUnlockRetryDispatchDecisionStatus.Held =>
                RuntimeUnlockRetryDispatchSuccessorDecisionStatus.Held,
            _ => RuntimeUnlockRetryDispatchSuccessorDecisionStatus.Rejected,
        };
        return Decision(
            status,
            authorization.Reason,
            request,
            authorization,
            authorization.State);
    }

    private static bool
        IsRuntimeUnlockRetryLifecyclePredecessorCurrentForSuccessor(
            FileTransferCoordinatorState state,
            RuntimeUnlockRetryLifecycleAttemptSnapshot predecessor,
            out string rejectionReason)
    {
        if (IsRuntimeUnlockRetryLifecycleAttemptCurrent(
                state,
                predecessor,
                out rejectionReason))
        {
            return true;
        }

        var authority = predecessor.Authority;
        if (rejectionReason != "retry_dispatch_runtime_scope_mismatch" ||
            predecessor.Settlement?.Outcome !=
                RuntimeUnlockRetryDispatchSettlementOutcome.MissingDispatch ||
            authority.ReleaseKind !=
                RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield ||
            state.IsTerminal ||
            state.RouteSelection.Route !=
                FileTransferRoute.PostTunaFallbackV6 ||
            state.RouteSelection.ProtocolVersion !=
                FileTransferProtocol.ProtocolVersionV6 ||
            state.CurrentRuntimeOwner is not { } owner ||
            state.CurrentRoutePublicationToken is not { } publication ||
            state.CurrentLeg is not { } leg ||
            leg.TransportEpochId <= authority.TransportEpoch ||
            !string.Equals(
                authority.SessionId,
                state.SessionId,
                StringComparison.Ordinal) ||
            !string.Equals(
                authority.TransferId,
                state.TransferId,
                StringComparison.Ordinal) ||
            authority.Direction != state.Direction ||
            authority.RuntimeOwner != owner ||
            authority.SourcePublicationAuthority != publication ||
            authority.Route != state.RouteSelection.Route ||
            !string.Equals(
                authority.RouteToken,
                state.RouteSelection.TelemetryToken,
                StringComparison.Ordinal) ||
            authority.ProtocolVersion != state.RouteSelection.ProtocolVersion ||
            authority.LiveRouteEpoch !=
                (state.CurrentLiveRouteEpoch?.EpochId ?? 0) ||
            authority.RuntimeGeneration != owner.RuntimeGeneration ||
            authority.TerminalGeneration != state.TerminalGeneration ||
            authority.LegGeneration != leg.Generation ||
            authority.BridgeRecoveryGeneration !=
                leg.BridgeRecoveryGeneration ||
            authority.RoutePublicationGeneration !=
                publication.PublicationGeneration ||
            authority.RouteCommitGeneration !=
                publication.RouteCommitGeneration)
        {
            return false;
        }

        rejectionReason = "accepted_same_leg_transport_epoch_carry_forward";
        return true;
    }

    internal static RuntimeUnlockRetryLifecycleTransitionDecision
        CloseRuntimeUnlockRetryDispatchChain(
            FileTransferCoordinatorState state,
            RuntimeUnlockRetryDispatchAuthorityRequest rejectedSuccessorRequest,
            string reason)
    {
        RuntimeUnlockRetryLifecycleTransitionDecision Decision(
            RuntimeUnlockRetryLifecycleTransitionStatus status,
            string decisionReason,
            FileTransferCoordinatorState? nextState = null)
            => new(status, decisionReason)
            {
                State = nextState ?? state,
            };

        var normalizedReason = NormalizeReason(reason);
        var lifecycle = GetRuntimeUnlockRetryLifecycle(state);
        if (lifecycle.PendingScopeInvalidationCommand is not null)
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_scope_invalidation_pending");
        }

        var finalAttempt = lifecycle.InstalledAttempt ??
                           lifecycle.LastTerminalAttempt;
        if (finalAttempt is not { } attempt ||
            attempt.Settlement is not
            {
                Outcome: RuntimeUnlockRetryDispatchSettlementOutcome.MissingDispatch,
            } settlement ||
            rejectedSuccessorRequest.IsDefault ||
            rejectedSuccessorRequest.PredecessorAuthorityId !=
                attempt.Authority.AuthorityId ||
            rejectedSuccessorRequest.DispatchAttemptGeneration !=
                attempt.Authority.DispatchAttemptGeneration + 1 ||
            rejectedSuccessorRequest.MissingDispatchSuccessorGeneration !=
                attempt.Authority.MissingDispatchSuccessorGeneration + 1 ||
            rejectedSuccessorRequest.MissingDispatchSuccessorGeneration <=
                RuntimeUnlockRetryMissingDispatchSuccessorLimit ||
            !string.Equals(
                normalizedReason,
                "retry_dispatch_missing_successor_limit_exhausted",
                StringComparison.Ordinal))
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_chain_closure_mismatch");
        }

        if (!IsRuntimeUnlockRetryLifecyclePredecessorCurrentForSuccessor(
                state,
                attempt,
                out var attemptCurrentnessReason))
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                attemptCurrentnessReason);
        }

        if (lifecycle.BoundedClosure is { } existingClosure)
        {
            if (existingClosure.FinalAttempt.Authority == attempt.Authority &&
                existingClosure.RejectedSuccessorRequest ==
                    rejectedSuccessorRequest)
            {
                return Decision(
                    RuntimeUnlockRetryLifecycleTransitionStatus.Idempotent,
                    "retry_dispatch_chain_already_closed");
            }

            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_chain_closure_conflict");
        }

        var closedAttempt = attempt with
        {
            State = RuntimeUnlockRetryLifecycleAttemptState.Closed,
            Settlement = settlement,
        };
        if (lifecycle.LastClosureGeneration == long.MaxValue)
        {
            return Decision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_chain_closure_generation_exhausted");
        }

        var closureGeneration =
            Math.Max(1, lifecycle.LastClosureGeneration + 1);
        var closure = new RuntimeUnlockRetryLifecycleBoundedClosure(
            closedAttempt,
            rejectedSuccessorRequest,
            closureGeneration,
            normalizedReason);
        var nextLifecycle = lifecycle with
        {
            PendingAttempt = null,
            InstalledAttempt = null,
            RetainedPredecessor = null,
            LastTerminalAttempt = closedAttempt,
            BoundedClosure = closure,
            LastClosureGeneration = closureGeneration,
        };
        return Decision(
            RuntimeUnlockRetryLifecycleTransitionStatus.Accepted,
            "retry_dispatch_chain_closed",
            ApplyRuntimeUnlockRetryLifecycle(state, nextLifecycle));
    }

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

    internal static bool TryGetFileTransferRouteTransitionPauseOwnership(
        FileTransferCoordinatorState state,
        FileTransferRouteTransitionAuthority authority,
        out FileTransferRouteTransitionPauseOwnership ownership,
        out string rejectionReason)
    {
        ownership = default;
        rejectionReason =
            "filetransfer_route_transition_pause_authority_not_current";
        var transitionState =
            state.PersistentState.FileTransferRouteTransition;

        if (transitionState.CurrentOperation is { } current &&
            current.Authority == authority)
        {
            var pauseBound =
                current.PauseBindReceipt is { } pauseReceipt &&
                pauseReceipt.Command.Authority == authority &&
                pauseReceipt.Status is (
                    FileTransferRouteTransitionPauseBindStatus.Installed or
                    FileTransferRouteTransitionPauseBindStatus.Idempotent) &&
                IsCanonicalFileTransferRouteTransitionPauseBindReceipt(
                    pauseReceipt);
            var preparedSupersession =
                current.State == FileTransferRouteTransitionState.Prepared &&
                pauseBound &&
                authority.PredecessorTransitionAuthorityId is { } predecessorId &&
                transitionState.LastTerminalOperation is
                {
                    State: FileTransferRouteTransitionState.Invalidated,
                    DispositionCommand.Kind:
                        FileTransferRouteTransitionDispositionKind.Superseded,
                } predecessor &&
                predecessor.Authority.AuthorityId == predecessorId;
            if ((current.State == FileTransferRouteTransitionState.Draining ||
                 preparedSupersession) &&
                IsFileTransferRouteTransitionSourceScopeCurrent(
                    state,
                    authority,
                    out rejectionReason))
            {
                ownership = new FileTransferRouteTransitionPauseOwnership(
                    authority,
                    current.State,
                    RetainedFrameHandoffPending: false,
                    DispositionHandoffPending: false);
                rejectionReason = "accepted";
                return true;
            }

            if (string.Equals(
                    rejectionReason,
                    "accepted",
                    StringComparison.Ordinal))
            {
                rejectionReason =
                    "filetransfer_route_transition_pause_state_not_owned";
            }

            return false;
        }

        if (transitionState.LastTerminalOperation is not { } terminal ||
            terminal.Authority != authority ||
            terminal.State is not (
                FileTransferRouteTransitionState.Committed or
                FileTransferRouteTransitionState.Invalidated))
        {
            return false;
        }

        var retainedFramePending =
            terminal.RetainedFrameApplyCommand is not null &&
            terminal.RetainedFrameApplyReceipt is null;
        var dispositionPending =
            terminal.DispositionCommand is not null &&
            terminal.DispositionReceipt is null;
        if (!retainedFramePending && !dispositionPending)
        {
            rejectionReason =
                "filetransfer_route_transition_pause_handoff_complete";
            return false;
        }

        if (terminal.DispositionCommand?.Kind ==
            FileTransferRouteTransitionDispositionKind.Superseded)
        {
            rejectionReason =
                "filetransfer_route_transition_pause_owned_by_successor";
            return false;
        }

        if (terminal.State == FileTransferRouteTransitionState.Committed)
        {
            if (!IsFileTransferRouteTransitionTargetScopeCurrent(
                    state,
                    terminal,
                    out rejectionReason))
            {
                return false;
            }
        }
        else if (!state.IsTerminal &&
                 !IsFileTransferRouteTransitionSourceScopeCurrent(
                     state,
                     authority,
                     out rejectionReason))
        {
            return false;
        }

        ownership = new FileTransferRouteTransitionPauseOwnership(
            authority,
            terminal.State,
            retainedFramePending,
            dispositionPending);
        rejectionReason = "accepted";
        return true;
    }

    private static bool IsFileTransferRouteTransitionTargetScopeCurrent(
        FileTransferCoordinatorState state,
        FileTransferRouteTransitionOperation operation,
        out string rejectionReason)
    {
        rejectionReason =
            "filetransfer_route_transition_target_scope_stale";
        var authority = operation.Authority;
        if (operation.RoutePublicationToken is not { } publication ||
            state.CurrentRuntimeOwner != publication.RuntimeOwner ||
            state.CurrentRoutePublicationToken != publication ||
            state.RouteSelection.Route != authority.TargetRoute ||
            state.RouteSelection.ProtocolVersion !=
                authority.TargetProtocolVersion ||
            ExpectedRoutePublicationTransport(state.RouteSelection.Route) !=
                authority.TargetTransport ||
            state.CurrentLiveRouteEpoch is not { } epoch ||
            state.CurrentLeg is not { } leg ||
            publication.LiveRouteEpoch != epoch.EpochId ||
            publication.LegGeneration != leg.Generation ||
            leg.RouteSelection != state.RouteSelection ||
            leg.ProtocolVersion != authority.TargetProtocolVersion ||
            leg.LiveRouteEpochId != epoch.EpochId ||
            state.CurrentDataSessionGeneration != authority.DataSessionGeneration ||
            !IsRuntimeOwnerCurrent(
                state,
                publication.RuntimeOwner,
                out rejectionReason) ||
            !IsRoutePublicationTokenCurrent(
                state,
                publication,
                out rejectionReason) ||
            operation.RouteCommitToken is { } routeCommit &&
                state.CurrentRouteCommit != routeCommit ||
            operation.RouteCommitToken is null &&
                state.CurrentRouteCommit is not null)
        {
            return false;
        }

        if (authority.ProofBoundary is
            FileTransferFallbackTransitionProofBoundary fallback)
        {
            if (leg.TransportEpochId != fallback.TargetTransportEpoch ||
                leg.BridgeRecoveryGeneration !=
                    fallback.TargetBridgeRecoveryGeneration)
            {
                rejectionReason =
                    "filetransfer_route_transition_target_transport_scope_stale";
                return false;
            }
        }
        else if (leg.TransportEpochId != 0 ||
                 leg.BridgeRecoveryGeneration != 0)
        {
            rejectionReason =
                "filetransfer_route_transition_target_transport_scope_stale";
            return false;
        }

        rejectionReason = "accepted";
        return true;
    }

    internal static bool TryGetPendingFileTransferRouteTransitionDisposition(
        FileTransferCoordinatorState state,
        out FileTransferRouteTransitionDispositionCommand command)
    {
        if (state.PersistentState.FileTransferRouteTransition.LastTerminalOperation is
            {
                DispositionCommand: { } pending,
                DispositionReceipt: null,
            } &&
            pending.Kind != FileTransferRouteTransitionDispositionKind.Superseded)
        {
            command = pending;
            return true;
        }

        command = default;
        return false;
    }

    internal static string CreateFileTransferRouteTransitionPhysicalDrainFingerprint(
        IReadOnlyList<long> capturedPhysicalSendLeaseIds)
    {
        ArgumentNullException.ThrowIfNull(capturedPhysicalSendLeaseIds);
        var canonical = string.Join(
            ",",
            capturedPhysicalSendLeaseIds.Select(id =>
                id.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)));
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static bool IsFileTransferRouteTransitionDispositionCommandValid(
        FileTransferRouteTransitionDispositionCommand command)
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
            FileTransferRouteTransitionDispositionKind.RolledBack or
            FileTransferRouteTransitionDispositionKind.TerminalInvalidated)
        {
            return command.RouteCommitToken is null &&
                   command.RoutePublicationToken is null &&
                   command.FinalProof is null;
        }

        if (command.Kind == FileTransferRouteTransitionDispositionKind.Superseded)
        {
            return command.RouteCommitToken is null &&
                   command.RoutePublicationToken is null &&
                   command.FinalProof is null;
        }

        if (command.Kind != FileTransferRouteTransitionDispositionKind.Committed ||
            command.RoutePublicationToken is not { IsDefault: false } publication ||
            publication.RuntimeOwner.Route != authority.TargetRoute ||
            publication.Route != authority.TargetRoute ||
            publication.ProtocolVersion != authority.TargetProtocolVersion ||
            publication.TargetRoute != authority.TargetRoute ||
            publication.TargetProtocolVersion != authority.TargetProtocolVersion ||
            publication.TargetTransport != authority.TargetTransport)
        {
            return false;
        }

        if (authority.TargetRoute == FileTransferRoute.PostTunaFallbackV6)
        {
            return command.RouteCommitToken is null &&
                   command.FinalProof is null &&
                   publication.ActionKind ==
                       FileTransferCoordinatorActionKind.RoutePublication;
        }

        return authority.TargetRoute == FileTransferRoute.FileTunaV4 &&
               command.RouteCommitToken is { IsDefault: false } commit &&
               command.FinalProof is
                   { PathProbeState: RuntimeUnlockPathProbeState.Acked } proof &&
               commit.RuntimeOwner == publication.RuntimeOwner &&
               commit.CommitGeneration == publication.RouteCommitGeneration &&
               commit.RuntimeUnlockAdmission == proof.AdmissionToken &&
               commit.TargetRoute == authority.TargetRoute &&
               commit.TargetProtocolVersion == authority.TargetProtocolVersion &&
               commit.TargetTransport == authority.TargetTransport &&
               publication.ActionKind ==
                   FileTransferCoordinatorActionKind.RouteCommitPublication;
    }

    internal static bool IsFileTransferRouteTransitionRetainedFrameApplyCommandValid(
        FileTransferRouteTransitionRetainedFrameApplyCommand command)
    {
        var authority = command.Authority;
        return !authority.IsDefault &&
               authority.TargetRoute == FileTransferRoute.PostTunaFallbackV6 &&
               authority.TargetProtocolVersion ==
                   FileTransferProtocol.ProtocolVersionV6 &&
               authority.TargetTransport == FileTransferTransportKind.RegularNkn &&
               authority.ProofBoundary is
                   FileTransferFallbackTransitionProofBoundary fallback &&
               fallback.RetainedFrameIdentity == command.FrameIdentity &&
               command.FrameIdentity.IsComplete &&
               !command.TargetRuntimeOwner.IsDefault &&
               !command.TargetRoutePublication.IsDefault &&
               string.Equals(
                   command.TargetRuntimeOwner.SessionId,
                   authority.SessionId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   command.TargetRuntimeOwner.TransferId,
                   authority.TransferId,
                   StringComparison.Ordinal) &&
               command.TargetRuntimeOwner.Direction == authority.Direction &&
               command.TargetRuntimeOwner.Route == authority.TargetRoute &&
               command.TargetRuntimeOwner.ProtocolVersion ==
                   authority.TargetProtocolVersion &&
               command.TargetRoutePublication.RuntimeOwner ==
                   command.TargetRuntimeOwner &&
               command.TargetRoutePublication.Route == authority.TargetRoute &&
               command.TargetRoutePublication.ProtocolVersion ==
                   authority.TargetProtocolVersion &&
               command.TargetRoutePublication.TargetRoute ==
                   authority.TargetRoute &&
               command.TargetRoutePublication.TargetProtocolVersion ==
                   authority.TargetProtocolVersion &&
               command.TargetRoutePublication.TargetTransport ==
                   authority.TargetTransport &&
               command.TargetRoutePublication.ActionKind ==
                   FileTransferCoordinatorActionKind.RoutePublication;
    }

    internal static FileTransferRouteTransitionDecision PrepareFileTransferRouteTransition(
        FileTransferCoordinatorState state,
        FileTransferRouteTransitionPrepareRequest request)
    {
        var transitionState = state.PersistentState.FileTransferRouteTransition;
        if (transitionState.CurrentOperation is { } current)
        {
            if (FileTransferRouteTransitionPrepareRequestMatchesAuthority(
                    request,
                    current.Authority) &&
                IsFileTransferRouteTransitionSourceScopeCurrent(
                    state,
                    current.Authority,
                    out _) &&
                TryValidateFileTransferRouteTransitionPrepareRequest(
                    state,
                    request,
                    out _,
                    allowCurrentOperation: true))
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
                State: FileTransferRouteTransitionState.Committed,
            } committed &&
            FileTransferRouteTransitionPrepareRequestMatchesAuthority(
                request,
                committed.Authority) &&
            IsCommittedFileTransferRouteTransitionTerminalEvidenceValid(
                committed))
        {
            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Idempotent,
                "filetransfer_route_transition_already_committed",
                committed.Authority,
                dispositionCommand: committed.DispositionCommand,
                retainedFrameApplyCommand:
                    committed.RetainedFrameApplyCommand);
        }

        if (transitionState.LastTerminalOperation is
            {
                State: FileTransferRouteTransitionState.Invalidated,
                DispositionCommand:
                {
                    Kind:
                        FileTransferRouteTransitionDispositionKind
                            .TerminalInvalidated,
                },
            } terminalInvalidated &&
            FileTransferRouteTransitionPrepareRequestMatchesAuthority(
                request,
                terminalInvalidated.Authority))
        {
            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Idempotent,
                "filetransfer_route_transition_terminally_invalidated",
                terminalInvalidated.Authority,
                dispositionCommand:
                    terminalInvalidated.DispositionCommand);
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

        var expectedPauseCommand = current.PauseBindReceipt is { } retainedPauseReceipt
            ? retainedPauseReceipt.Command
            : new FileTransferRouteTransitionPauseBindCommand(authority);
        if (pauseBindReceipt.Command != expectedPauseCommand ||
            current.PauseBindReceipt is { } committedPauseReceipt &&
            !IsExactOrCanonicalIdempotentFileTransferRouteTransitionPauseBindReceipt(
                committedPauseReceipt,
                pauseBindReceipt) ||
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
                    pending.SuccessorAuthority) &&
                transitionState.CurrentOperation is { } currentPredecessor &&
                currentPredecessor.Authority == predecessorAuthority &&
                IsFileTransferRouteTransitionSourceScopeCurrent(
                    state,
                    predecessorAuthority,
                    out _) &&
                TryValidateFileTransferRouteTransitionPrepareRequest(
                    state,
                    successorRequest,
                    out _,
                    allowCurrentOperation: true))
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
        var pauseRebind = new FileTransferRouteTransitionPauseBindCommand(successor)
        {
            PredecessorAuthority = predecessorAuthority,
        };
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
        if (transitionState.PendingSupersession is null &&
            transitionState.CurrentOperation is { } installedSuccessor &&
            transitionState.LastTerminalOperation is
                {
                    State: FileTransferRouteTransitionState.Invalidated,
                    DispositionCommand.Kind:
                        FileTransferRouteTransitionDispositionKind.Superseded,
                } supersededPredecessor &&
            installedSuccessor.Authority ==
                pauseRebindReceipt.Command.Authority &&
            installedSuccessor.Authority.Kind ==
                FileTransferRouteTransitionKind
                    .UncommittedTunaToPostTunaFallbackV6 &&
            pauseRebindReceipt.Command.PredecessorAuthority ==
                supersededPredecessor.Authority &&
            installedSuccessor.PauseBindReceipt is { } installedReceipt &&
            IsExactOrCanonicalIdempotentFileTransferRouteTransitionPauseBindReceipt(
                installedReceipt,
                pauseRebindReceipt))
        {
            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Idempotent,
                "filetransfer_route_transition_supersession_already_committed",
                installedSuccessor.Authority,
                pauseRebindReceipt.Command,
                installedSuccessor.DrainCommand,
                supersededPredecessor.DispositionCommand);
        }

        if (transitionState.PendingSupersession is not { } pending ||
            transitionState.CurrentOperation is not { } predecessor ||
            predecessor.Authority != pending.PredecessorAuthority)
        {
            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Rejected,
                "filetransfer_route_transition_supersession_not_pending");
        }

        if (!IsFileTransferRouteTransitionSourceScopeCurrent(
                state,
                predecessor.Authority,
                out var supersessionScopeRejection))
        {
            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Rejected,
                supersessionScopeRejection);
        }

        var expectedCommand = pending.PauseRebindCommand;
        if (pauseRebindReceipt.Command != expectedCommand ||
            expectedCommand.PredecessorAuthority != pending.PredecessorAuthority ||
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
        if (!IsFileTransferRouteTransitionDispositionCommandValid(
                supersededDisposition))
        {
            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Rejected,
                "filetransfer_route_transition_supersession_disposition_invalid");
        }
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
            successor.State == FileTransferRouteTransitionState.Draining
                ? FileTransferRouteTransitionDecisionStatus.Draining
                : FileTransferRouteTransitionDecisionStatus.Prepared,
            "filetransfer_route_transition_supersession_committed",
            successor.Authority,
            expectedCommand,
            successorDrain,
            supersededDisposition);
    }

    internal static RegularV4FrontierRepairPressureBindingDecision
        PrepareFileTransferRouteTransitionPressureSettlement(
            FileTransferCoordinatorState state,
            FileTransferRouteTransitionAuthority authority,
            FileTransferRouteTransitionPhysicalDrainReceipt drainReceipt,
            RuntimeUnlockRouteCommitProof? finalProof = null)
    {
        if (!TryValidateFileTransferRouteTransitionDrainReceipt(
                state,
                authority,
                drainReceipt,
                out var rejectionReason) ||
            !TryValidateFileTransferRouteTransitionCommitProof(
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

        var pressureState =
            state.PersistentState.RegularV4FrontierRepairPressure;
        if (pressureState.PendingBinding is not null ||
            pressureState.RetainedPredecessorBinding is not null)
        {
            return RegularV4PressureBindingDecision(
                state,
                RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
                "filetransfer_route_transition_pressure_lineage_pending");
        }

        if (pressureState.InstalledBinding is not { } installed)
        {
            return RegularV4PressureBindingDecision(
                state,
                RegularV4FrontierRepairPressureBindingDecisionStatus.Idempotent,
                "filetransfer_route_transition_pressure_not_installed");
        }

        return PrepareRegularV4FrontierRepairPressureSettlement(
            state,
            CreateFileTransferRouteTransitionPressureSettlement(
                installed.Authority));
    }

    internal static FileTransferRouteTransitionDecision CommitFileTransferRouteTransition(
        FileTransferCoordinatorState state,
        FileTransferRouteTransitionAuthority authority,
        FileTransferRouteTransitionPhysicalDrainReceipt drainReceipt,
        RuntimeUnlockRouteCommitProof? finalProof = null,
        FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt?
            pressureSettlementReceipt = null)
    {
        var transitionState = state.PersistentState.FileTransferRouteTransition;
        if (transitionState.LastTerminalOperation is
            {
                State: FileTransferRouteTransitionState.Committed,
                DrainReceipt: { } committedDrain,
            } terminal &&
            terminal.Authority == authority &&
            IsExactOrCanonicalIdempotentFileTransferRouteTransitionDrainReceipt(
                committedDrain,
                drainReceipt) &&
            terminal.FinalProof == finalProof &&
            IsExactFileTransferRouteTransitionPressureReceiptDuplicate(
                terminal.PressureSettlementReceipt,
                pressureSettlementReceipt) &&
            IsCommittedFileTransferRouteTransitionTerminalEvidenceValid(terminal))
        {
            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Idempotent,
                "filetransfer_route_transition_already_committed",
                authority,
                drainCommand: terminal.DrainCommand,
                dispositionCommand: terminal.DispositionCommand,
                retainedFrameApplyCommand: terminal.RetainedFrameApplyCommand);
        }

        if (RequiresRuntimeUnlockRetryLifecycleScopeInvalidation(state))
        {
            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Rejected,
                "runtime_unlock_retry_scope_invalidation_required");
        }

        if (!TryValidateFileTransferRouteTransitionDrainReceipt(
                state,
                authority,
                drainReceipt,
                out var rejectionReason) ||
            !TryValidateFileTransferRouteTransitionCommitProof(
                state,
                authority,
                finalProof,
                out rejectionReason))
        {
            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Rejected,
                rejectionReason);
        }

        var projectedState = state;
        var pressureState = state.PersistentState.RegularV4FrontierRepairPressure;
        if (pressureState.PendingBinding is not null ||
            pressureState.RetainedPredecessorBinding is not null)
        {
            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Rejected,
                "filetransfer_route_transition_pressure_lineage_pending");
        }

        if (pressureState.InstalledBinding is { } installed)
        {
            var expectedSettlement =
                CreateFileTransferRouteTransitionPressureSettlement(
                    installed.Authority);
            var preparedPressure =
                PrepareRegularV4FrontierRepairPressureSettlement(state, expectedSettlement);
            if (pressureSettlementReceipt is null ||
                preparedPressure.Status !=
                    RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted ||
                preparedPressure.SettlementCommand is not { } expectedCommand ||
                pressureSettlementReceipt.Command != expectedCommand ||
                !IsCanonicalFileTransferRouteTransitionPressureSettlementReceipt(
                    pressureSettlementReceipt))
            {
                return FileTransferRouteTransitionDecisionFor(
                    state,
                    FileTransferRouteTransitionDecisionStatus.Rejected,
                    "filetransfer_route_transition_pressure_apply_receipt_missing");
            }

            var pressureCommit = CommitRegularV4FrontierRepairPressureSettlement(
                state,
                pressureSettlementReceipt);
            if (pressureCommit.Status !=
                RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted)
            {
                return FileTransferRouteTransitionDecisionFor(
                    state,
                    FileTransferRouteTransitionDecisionStatus.Rejected,
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
            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Rejected,
                "filetransfer_route_transition_pressure_apply_receipt_unexpected");
        }

        projectedState = CloneCoordinatorStateGraph(projectedState);
        projectedState = projectedState with
        {
            PersistentState = projectedState.PersistentState with
            {
                FileTransferRouteTransition = transitionState with
                {
                    CurrentOperation = null,
                    PendingSupersession = null,
                },
            },
        };
        var targetSelection = FileTransferRouteResolver.Resolve(authority.TargetRoute);
        var toTuna = authority.TargetRoute == FileTransferRoute.FileTunaV4;
        var fallbackProof =
            authority.ProofBoundary as FileTransferFallbackTransitionProofBoundary;
        var coordinatorEvent = new FileTransferCoordinatorEvent(
            toTuna
                ? FileTransferCoordinatorEventKind.TunaActivated
                : FileTransferCoordinatorEventKind.TunaLost,
            targetSelection,
            authority.HandoffKind,
            authority.TargetTransport,
            NormalizeReason(authority.Reason),
            toTuna ? FileTransferLegState.Active : FileTransferLegState.CheckpointPending,
            CanSendData: toTuna,
            authority.DurableCheckpoint.ContiguousCommittedChunkIndex,
            authority.DurableCheckpoint.DurableHighestChunkIndex,
            TransportEpochId:
                toTuna ? 0 : fallbackProof!.TargetTransportEpoch,
            BridgeRecoveryGeneration:
                toTuna ? 0 : fallbackProof!.TargetBridgeRecoveryGeneration);
        var routeDecision = TransitionToRoute(
            coordinatorEvent,
            projectedState,
            NormalizeReason(authority.Reason),
            recoverEpoch: toTuna);
        if (routeDecision.MutationRejectedReason is { } routeRejection)
        {
            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Rejected,
                routeRejection);
        }

        var committedState = routeDecision.State;
        if (committedState.CurrentRuntimeOwner is not { } runtimeOwner ||
            committedState.CurrentLeg is not { } targetLeg)
        {
            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Rejected,
                "filetransfer_route_transition_target_scope_missing");
        }

        RouteCommitToken? routeCommitToken = null;
        RoutePublicationToken publicationToken;
        if (toTuna)
        {
            var proof = finalProof!.Value;
            var activation =
                (FileTransferTunaActivationTransitionProofBoundary)
                authority.ProofBoundary;
            var commitGeneration = Math.Max(1, state.LastRouteCommitGeneration + 1);
            routeCommitToken = new RouteCommitToken(
                committedState.SessionId,
                committedState.TransferId,
                committedState.Direction,
                runtimeOwner,
                activation.RuntimeUnlockAdmission,
                committedState.RouteSelection.Route,
                committedState.RouteSelection.TelemetryToken,
                committedState.RouteSelection.ProtocolVersion,
                committedState.CurrentLiveRouteEpoch?.EpochId ?? 0,
                runtimeOwner.RuntimeGeneration,
                committedState.TerminalGeneration,
                targetLeg.Generation,
                proof.TransactionGeneration,
                proof.OfferGeneration,
                proof.TunaPathLeaseGeneration,
                proof.TunaPathLeaseListenerRunId,
                proof.SamePathTunaProbeAckId ?? proof.PathProbeId ?? string.Empty,
                authority.TargetRoute,
                authority.TargetProtocolVersion,
                authority.TargetTransport,
                commitGeneration,
                NormalizeReason(authority.Reason));
            committedState = committedState with
            {
                CurrentRouteCommit = routeCommitToken,
                LastRouteCommitGeneration = commitGeneration,
                CurrentRuntimeUnlockAdmission = null,
                CurrentRuntimeUnlockPendingPreCommitProbe = null,
            };
            var publicationRequest = new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RouteCommitPublication,
                Source: "filetransfer_route_transition",
                Reason: authority.Reason,
                RuntimeOwner: runtimeOwner,
                Route: committedState.RouteSelection.Route,
                ProtocolVersion: committedState.RouteSelection.ProtocolVersion,
                LiveRouteEpoch: committedState.CurrentLiveRouteEpoch?.EpochId ?? 0,
                LegGeneration: targetLeg.Generation,
                RuntimeGeneration: runtimeOwner.RuntimeGeneration,
                TerminalGeneration: committedState.TerminalGeneration,
                RuntimeUnlockAdmissionToken: activation.RuntimeUnlockAdmission,
                RouteCommitToken: routeCommitToken,
                RuntimeUnlockTransactionGeneration: proof.TransactionGeneration,
                RuntimeUnlockOfferGeneration: proof.OfferGeneration,
                RuntimeUnlockTunaPathLeaseGeneration: proof.TunaPathLeaseGeneration,
                RuntimeUnlockTunaPathLeaseListenerRunId:
                    proof.TunaPathLeaseListenerRunId,
                RuntimeUnlockProbeId: proof.PathProbeId,
                RuntimeUnlockSamePathTunaProbeAckId:
                    proof.SamePathTunaProbeAckId,
                RouteCommitGeneration: commitGeneration,
                TargetRoute: authority.TargetRoute,
                TargetProtocolVersion: authority.TargetProtocolVersion,
                TargetTransport: authority.TargetTransport);
            publicationToken = IssueRoutePublicationToken(
                committedState,
                publicationRequest,
                Math.Max(1, state.LastRoutePublicationGeneration + 1))!.Value;
            committedState = ApplyIssuedRoutePublicationAuthority(
                committedState,
                CreateRoutePublicationDecision(
                    committedState,
                    FileTransferCoordinatorActionKind.RouteCommitPublication,
                    accepted: true,
                    rejectionReason: "accepted",
                    publicationToken));
        }
        else
        {
            committedState = committedState with
            {
                CurrentRouteCommit = null,
                CurrentRuntimeUnlockAdmission = null,
                CurrentRuntimeUnlockPendingPreCommitProbe = null,
            };
            var publicationRequest = new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RoutePublication,
                Source: "filetransfer_route_transition",
                Reason: authority.Reason,
                RuntimeOwner: runtimeOwner,
                Route: committedState.RouteSelection.Route,
                ProtocolVersion: committedState.RouteSelection.ProtocolVersion,
                LiveRouteEpoch: committedState.CurrentLiveRouteEpoch?.EpochId ?? 0,
                LegGeneration: targetLeg.Generation,
                RuntimeGeneration: runtimeOwner.RuntimeGeneration,
                TerminalGeneration: committedState.TerminalGeneration,
                TargetRoute: authority.TargetRoute,
                TargetProtocolVersion: authority.TargetProtocolVersion,
                TargetTransport: authority.TargetTransport);
            publicationToken = IssueRoutePublicationToken(
                committedState,
                publicationRequest,
                Math.Max(1, state.LastRoutePublicationGeneration + 1))!.Value;
            committedState = ApplyIssuedRoutePublicationAuthority(
                committedState,
                CreateRoutePublicationDecision(
                    committedState,
                    FileTransferCoordinatorActionKind.RoutePublication,
                    accepted: true,
                    rejectionReason: "accepted",
                    publicationToken));
        }

        var disposition = new FileTransferRouteTransitionDispositionCommand(
            authority,
            FileTransferRouteTransitionDispositionKind.Committed,
            routeCommitToken,
            publicationToken,
            "filetransfer_route_transition_committed")
        {
            FinalProof = finalProof,
        };
        FileTransferRouteTransitionRetainedFrameApplyCommand? frameApplyCommand =
            fallbackProof is null
                ? null
                : new FileTransferRouteTransitionRetainedFrameApplyCommand(
                    authority,
                    runtimeOwner,
                    publicationToken,
                    fallbackProof.RetainedFrameIdentity);
        if (!IsFileTransferRouteTransitionDispositionCommandValid(disposition) ||
            frameApplyCommand is { } retainedFrameCommand &&
            !IsFileTransferRouteTransitionRetainedFrameApplyCommandValid(
                retainedFrameCommand))
        {
            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Rejected,
                "filetransfer_route_transition_commit_evidence_invalid");
        }
        var terminalOperation =
            transitionState.CurrentOperation!.Value with
            {
                State = FileTransferRouteTransitionState.Committed,
                DrainReceipt =
                    SnapshotFileTransferRouteTransitionDrainReceipt(drainReceipt),
                FinalProof = finalProof,
                PressureSettlementReceipt = pressureSettlementReceipt,
                RouteCommitToken = routeCommitToken,
                RoutePublicationToken = publicationToken,
                DispositionCommand = disposition,
                RetainedFrameApplyCommand = frameApplyCommand,
            };
        committedState = committedState with
        {
            PersistentState = committedState.PersistentState with
            {
                FileTransferRouteTransition = transitionState with
                {
                    CurrentOperation = null,
                    PendingSupersession = null,
                    LastTerminalOperation = terminalOperation,
                },
            },
        };
        routeDecision = routeDecision with
        {
            State = committedState,
            RuntimeUnlockCommitAccepted = toTuna,
            RuntimeUnlockCommitRejectedReason = null,
            RouteCommitToken = routeCommitToken,
        };
        return FileTransferRouteTransitionDecisionFor(
            committedState,
            FileTransferRouteTransitionDecisionStatus.Committed,
            "filetransfer_route_transition_committed",
            authority,
            drainCommand: terminalOperation.DrainCommand,
            dispositionCommand: disposition,
            retainedFrameApplyCommand: frameApplyCommand,
            routeDecision: routeDecision);
    }

    internal static FileTransferRouteTransitionDecision
        ApplyFileTransferRouteTransitionRetainedFrameReceipt(
            FileTransferCoordinatorState state,
            FileTransferRouteTransitionRetainedFrameApplyReceipt receipt)
    {
        var transitionState = state.PersistentState.FileTransferRouteTransition;
        if (!IsFileTransferRouteTransitionRetainedFrameApplyCommandValid(
                receipt.Command) ||
            transitionState.LastTerminalOperation is not
            {
                State: FileTransferRouteTransitionState.Committed,
                RetainedFrameApplyCommand: { } expectedCommand,
            } terminal ||
            receipt.Command != expectedCommand ||
            terminal.Authority.TargetRoute != FileTransferRoute.PostTunaFallbackV6)
        {
            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Rejected,
                "filetransfer_route_transition_retained_frame_receipt_not_current");
        }

        if (terminal.RetainedFrameApplyReceipt is { } applied)
        {
            if (receipt == applied ||
                receipt.Command == applied.Command &&
                receipt.Status ==
                    FileTransferRouteTransitionRetainedFrameApplyStatus.Idempotent &&
                string.Equals(
                    receipt.Reason,
                    "filetransfer_route_transition_retained_frame_apply_idempotent",
                    StringComparison.Ordinal))
            {
                return FileTransferRouteTransitionDecisionFor(
                    state,
                    FileTransferRouteTransitionDecisionStatus.Idempotent,
                    "filetransfer_route_transition_retained_frame_already_applied",
                    terminal.Authority,
                    drainCommand: terminal.DrainCommand,
                    dispositionCommand: terminal.DispositionCommand,
                    retainedFrameApplyCommand: expectedCommand);
            }

            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Rejected,
                "filetransfer_route_transition_retained_frame_duplicate_mismatch");
        }

        if (receipt.Status is not (
                FileTransferRouteTransitionRetainedFrameApplyStatus.Applied or
                FileTransferRouteTransitionRetainedFrameApplyStatus.Idempotent) ||
            !string.Equals(
                receipt.Reason,
                receipt.Status ==
                    FileTransferRouteTransitionRetainedFrameApplyStatus.Applied
                    ? "filetransfer_route_transition_retained_frame_applied"
                    : "filetransfer_route_transition_retained_frame_apply_idempotent",
                StringComparison.Ordinal) ||
            state.CurrentRuntimeOwner != expectedCommand.TargetRuntimeOwner ||
            state.CurrentRoutePublicationToken !=
                expectedCommand.TargetRoutePublication ||
            !IsRuntimeOwnerCurrent(
                state,
                expectedCommand.TargetRuntimeOwner,
                out _) ||
            !IsRoutePublicationTokenCurrent(
                state,
                expectedCommand.TargetRoutePublication,
                out _))
        {
            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Rejected,
                "filetransfer_route_transition_retained_frame_receipt_rejected");
        }

        var terminalWithReceipt =
            terminal with { RetainedFrameApplyReceipt = receipt };
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
            FileTransferRouteTransitionDecisionStatus.Committed,
            "filetransfer_route_transition_retained_frame_receipt_applied",
            terminal.Authority,
            drainCommand: terminal.DrainCommand,
            dispositionCommand: terminal.DispositionCommand,
            retainedFrameApplyCommand: expectedCommand);
    }

    internal static FileTransferRouteTransitionDecision
        InvalidateFileTransferRouteTransition(
            FileTransferCoordinatorState state,
            FileTransferRouteTransitionAuthority authority,
            bool terminalInvalidation,
            string reason)
    {
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
        if (!IsFileTransferRouteTransitionDispositionCommandValid(receipt.Command))
        {
            return FileTransferRouteTransitionDecisionFor(
                state,
                FileTransferRouteTransitionDecisionStatus.Rejected,
                "filetransfer_route_transition_disposition_command_invalid");
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

    public static FileTransferCoordinatorDecision Apply(
        FileTransferCoordinatorEvent coordinatorEvent,
        FileTransferCoordinatorState state)
    {
        state = InvalidateFallbackTailYieldForDataSessionReplacement(state);
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
            FileTransferCoordinatorEventKind.TunaActivated or
            FileTransferCoordinatorEventKind.TunaLost =>
                MutationRejected(
                    state,
                    reason,
                    "filetransfer_route_transition_commit_required"),
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
            FileTransferCoordinatorEventKind.BridgeRecoveryStarted or
            FileTransferCoordinatorEventKind.BridgeRecoveryCompleted or
            FileTransferCoordinatorEventKind.BridgeRecoveryExhausted =>
                PassThrough(workingState, reason),
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
            FileTransferCoordinatorEventKind.FallbackCheckpointRequested or
            FileTransferCoordinatorEventKind.FallbackCheckpointAccepted or
            FileTransferCoordinatorEventKind.FallbackCheckpointRetired or
            FileTransferCoordinatorEventKind.FallbackTailQuiescent or
            FileTransferCoordinatorEventKind.FallbackTailFailed or
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
        state = InvalidateFallbackTailYieldForDataSessionReplacement(state);
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

        if (request.Kind is
                FileTransferCoordinatorActionKind.FallbackTailQuiescent or
                FileTransferCoordinatorActionKind.FallbackTailFailed)
        {
            var requestedYieldKind =
                request.FallbackTailYieldKind ??
                (request.Kind ==
                    FileTransferCoordinatorActionKind.FallbackTailFailed
                    ? FallbackTailYieldKind.Failed
                    : FallbackTailYieldKind.Quiescent);
            var requestedPredecessor =
                request.FallbackTailYieldTransportEpochReauthorization
                    ?.PredecessorDecision.AuthorityId ??
                request.PredecessorAuthorityId;
            if (TryGetExactCurrentFallbackTailYieldDecision(
                    state,
                    requestedYieldKind,
                    requestedPredecessor,
                    request.FallbackTailYieldTransportEpochReauthorization,
                    out var existingYield))
            {
                return ActionDecision(
                    state,
                    request.Kind,
                    accepted: true,
                    rejectionReason: "accepted",
                    fallbackTailYieldDecision: existingYield);
            }

            if (state.PersistentState.FallbackTailYield.CurrentOperation is
                    { } currentOperation &&
                IsFallbackTailYieldScopeCurrent(
                    state,
                    currentOperation.Scope,
                    out _))
            {
                return ActionDecision(
                    state,
                    request.Kind,
                    accepted: false,
                    rejectionReason:
                        "fallback_tail_yield_operation_conflict");
            }

            if (!TryCreateCurrentFallbackTailYieldScope(
                    state,
                    out _))
            {
                return ActionDecision(
                    state,
                    request.Kind,
                    accepted: false,
                    rejectionReason:
                        "fallback_tail_yield_execution_scope_incomplete");
            }
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
                    Math.Max(1, GetLastFallbackTailYieldGeneration(state) + 1),
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
                request.TargetTransport,
                request.TargetTransportEpochId)
            : (TransportEpochDecision?)null;
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
            yieldDecision,
            request.FallbackTailYieldTransportEpochReauthorization);
        acceptedState = ApplyIssuedRepairOrTransportAuthority(
            acceptedState,
            repairReplayDecision,
            transportEpochDecision);
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
        if (GetPendingFallbackTailYieldTransportEpochReauthorization(state) is not { } pending ||
            pending != pendingReauthorization ||
            request.PredecessorDecision != pending.PredecessorDecision ||
            request.SourceRuntimeOwner != pending.SourceRuntimeOwner ||
            request.SourceRoutePublicationToken != pending.SourceRoutePublicationToken ||
            request.SourceTransportEpoch != pending.SourceTransportEpoch)
        {
            rejectionReason = "fallback_tail_yield_transport_epoch_reauthorization_restart_lineage_mismatch";
            return false;
        }

        var exactReboundTarget =
            state.CurrentLeg is { } exactTargetLeg &&
            request.TargetTransportEpoch ==
                pending.TargetTransportEpoch &&
            exactTargetLeg.TransportEpochId ==
                pending.TargetTransportEpoch;
        var newerTarget =
            state.CurrentLeg is { } advanceTargetLeg &&
            request.TargetTransportEpoch >
                pending.TargetTransportEpoch &&
            request.TargetTransportEpoch >
                advanceTargetLeg.TransportEpochId;
        var transportDecisionReplacement =
            request.TargetTransportEpochDecision !=
                pending.TargetTransportEpochDecision;
        if (!IsFallbackTailYieldTransportEpochReauthorizationCurrent(
                state,
                pending,
                allowTransportEpochDecisionReplacement:
                    transportDecisionReplacement,
                out rejectionReason))
        {
            return false;
        }

        if (transportDecisionReplacement &&
            (state.CurrentTransportEpochDecision !=
                 request.TargetTransportEpochDecision ||
             request.TargetTransportEpochDecision.IsDefault ||
             !request.TargetTransportEpochDecision.Accepted ||
             request.TargetTransportEpochDecision.ActionKind !=
                 TransportEpochActionKind.Adopt ||
             request.TargetTransportEpochDecision.TransportEpoch !=
                 pending.TargetTransportEpoch ||
             request.TargetTransportEpochDecision.TargetTransportEpoch !=
                 request.TargetTransportEpoch ||
             request.TargetTransportEpochDecision.TargetRoute !=
                 FileTransferRoute.PostTunaFallbackV6 ||
             request.TargetTransportEpochDecision
                     .TargetProtocolVersion !=
                 FileTransferProtocol.ProtocolVersionV6 ||
             request.TargetTransportEpochDecision.TargetTransport !=
                 FileTransferTransportKind.RegularNkn))
        {
            rejectionReason =
                "fallback_tail_yield_transport_epoch_reauthorization_restart_transport_decision_mismatch";
            return false;
        }

        if (state.CurrentLeg is not { } currentLeg ||
            state.RouteSelection.Route != FileTransferRoute.PostTunaFallbackV6 ||
            state.RouteSelection.ProtocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            coordinatorEvent.RouteSelection != state.RouteSelection ||
            (!exactReboundTarget && !newerTarget) ||
            (!transportDecisionReplacement &&
             request.TargetTransportEpochDecision !=
                 pending.TargetTransportEpochDecision) ||
            coordinatorEvent.TransportEpochId != request.TargetTransportEpoch ||
            coordinatorEvent.BridgeRecoveryGeneration < currentLeg.BridgeRecoveryGeneration ||
            HasFallbackTailPairedReleaseForDecision(
                state,
                request.PredecessorDecision))
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
            HasCurrentFallbackTailPairedRelease(state));

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
            PairedFallbackYieldGranted =
                HasCurrentFallbackTailPairedRelease(state),
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
    {
        var withoutFallbackAuthority = state with
        {
            CurrentFallbackControlPlaneToken = null,
            CurrentFallbackCheckpointLease = null,
            CurrentFallbackControlPlanePressureToken = null,
        };
        withoutFallbackAuthority = InvalidateFallbackTailYieldSubstate(
            withoutFallbackAuthority,
            state.IsTerminal
                ? "terminal_state"
                : "runtime_scope_replaced");
        return InvalidateRecoveryAuthority(withoutFallbackAuthority);
    }

    private static FileTransferCoordinatorState InvalidateRecoveryAuthority(
        FileTransferCoordinatorState state)
    {
        var recoveryState =
            state.PersistentState.FileTransferReceiveRecovery;
        if (recoveryState.CurrentOperation is { } currentOperation)
        {
            var invalidationReason = state.IsTerminal
                ? "terminal_state"
                : "runtime_scope_replaced";
            if (currentOperation.ClosureState ==
                    FileTransferReceiveRecoveryClosureState.Reserved &&
                currentOperation.ReservedRetryRequest is
                    { } reservedRequest &&
                currentOperation.ReservedRetryAuthority is
                    { } reservedAuthority)
            {
                var retryLifecycle =
                    GetRuntimeUnlockRetryLifecycle(state);
                if (retryLifecycle.PendingAttempt is
                    { } pendingAttempt &&
                    pendingAttempt.Request == reservedRequest &&
                    pendingAttempt.Authority == reservedAuthority)
                {
                    var invalidatedAttempt = pendingAttempt with
                    {
                        State =
                            RuntimeUnlockRetryLifecycleAttemptState.Invalidated,
                    };
                    state = ApplyRuntimeUnlockRetryLifecycle(
                        state,
                        retryLifecycle with
                        {
                            PendingAttempt = null,
                            LastTerminalAttempt = invalidatedAttempt,
                            AttemptHistory =
                                AppendRuntimeUnlockRetryLifecycleAttemptHistory(
                                    retryLifecycle.AttemptHistory,
                                    invalidatedAttempt),
                        });
                    recoveryState =
                        state.PersistentState.FileTransferReceiveRecovery;
                }
            }

            var invalidatedOperation = currentOperation with
            {
                ExecutionState =
                    FileTransferReceiveRecoveryExecutionState.Invalidated,
                ClosureState =
                    FileTransferReceiveRecoveryClosureState.None,
                HeldRetryRequest = null,
                ReservedRetryRequest = null,
                ReservedRetryAuthority = null,
                InvalidationReason = invalidationReason,
            };
            var terminalOfferHold =
                recoveryState.CurrentInitialActivationHold is
                    { } currentOfferHold &&
                RuntimeUnlockInitialActivationHoldMatchesOperation(
                    currentOfferHold,
                    currentOperation)
                    ? currentOfferHold with
                    {
                        Status =
                            RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus
                                .Invalidated,
                        Reason = invalidationReason,
                    }
                    : (RuntimeUnlockInitialActivationReceiveRecoveryHold?)null;
            recoveryState = recoveryState with
            {
                CurrentOperation = null,
                LastTerminalOperation = invalidatedOperation,
                CurrentInitialActivationHold =
                    terminalOfferHold is null
                        ? recoveryState.CurrentInitialActivationHold
                        : null,
                LastInitialActivationHold =
                    terminalOfferHold ??
                    recoveryState.LastInitialActivationHold,
            };
        }

        var retryLifecycleWithoutRepairRelease =
            GetRuntimeUnlockRetryLifecycle(state) with
            {
                RepairSettlementRelease = null,
            };
        var invalidated = state with
        {
            CurrentRepairReplayDecision = null,
            CurrentTransportEpochDecision = null,
            PersistentState = state.PersistentState with
            {
                FileTransferReceiveRecovery = recoveryState,
                RuntimeUnlockRetryLifecycle =
                    retryLifecycleWithoutRepairRelease,
            },
        };
        return invalidated;
    }

    private static bool
        RuntimeUnlockInitialActivationHoldMatchesOperation(
            RuntimeUnlockInitialActivationReceiveRecoveryHold hold,
            in FileTransferReceiveRecoveryOperation operation)
        => hold.ReceiveRecoveryAuthority == operation.Authority &&
           string.Equals(
               hold.Request.SessionId,
               operation.Authority.Observation.SessionId,
               StringComparison.Ordinal) &&
           string.Equals(
               hold.Request.TransferId,
               operation.Authority.Observation.TransferId,
               StringComparison.Ordinal) &&
           hold.Request.Direction ==
               operation.Authority.Observation.Direction;

    private static bool RequiresRuntimeUnlockRetryLifecycleScopeInvalidation(
        FileTransferCoordinatorState state)
    {
        var lifecycle = GetRuntimeUnlockRetryLifecycle(state);
        return lifecycle.PendingAttempt is not null ||
               GetRuntimeUnlockRetryLifecycleAttemptForScopeInvalidation(
                   lifecycle) is not null ||
               lifecycle.RetainedPredecessor is not null ||
               lifecycle.BoundedClosure is not null ||
               lifecycle.PendingScopeInvalidationCommand is not null;
    }

    private static RuntimeUnlockRetryLifecycleAttemptSnapshot?
        GetRuntimeUnlockRetryLifecycleAttemptForScopeInvalidation(
            RuntimeUnlockRetryLifecycleSubstate lifecycle)
    {
        if (lifecycle.InstalledAttempt is { } installed)
        {
            return installed;
        }

        if (lifecycle.PendingAttempt is null &&
            lifecycle.RetainedPredecessor is null &&
            lifecycle.LastTerminalAttempt is
            {
                State: RuntimeUnlockRetryLifecycleAttemptState.Settled,
                Settlement:
                {
                    Outcome:
                        RuntimeUnlockRetryDispatchSettlementOutcome
                            .DispatchObserved,
                },
            } dispatchObserved)
        {
            if (lifecycle.PendingScopeInvalidationCommand is null &&
                lifecycle.LastScopeInvalidationReceipt is
                {
                    Accepted: true,
                    Command.InstalledAttempt: { } invalidatedAttempt,
                } &&
                invalidatedAttempt == dispatchObserved)
            {
                return null;
            }

            return dispatchObserved;
        }

        return null;
    }

    private static FileTransferCoordinatorState InvalidateRoutePublicationAuthority(FileTransferCoordinatorState state)
    {
        var invalidated = state with
        {
            CurrentRoutePublicationToken = null,
        };
        return InvalidateFallbackTailYieldSubstate(
            invalidated,
            state.IsTerminal
                ? "terminal_state"
                : "route_publication_replaced");
    }

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

        if (state.PersistentState.FileTransferReceiveRecovery
                .CurrentOperation is { } recoveryOperation &&
            recoveryOperation.Authority.Observation.RoutePublication != token)
        {
            state = InvalidateRecoveryAuthority(state);
        }

        var pendingReauthorization =
            GetPendingFallbackTailYieldTransportEpochReauthorization(state);
        var pendingReauthorizationGeneration =
            state.PersistentState.FallbackTailYield
                .LastReauthorizationGeneration;
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
                pendingReauthorizationGeneration = Math.Max(
                    pending.ReauthorizationGeneration + 1,
                    pendingReauthorizationGeneration + 1);
                pendingReauthorization = pending with
                {
                    TargetRoutePublicationToken = token,
                    ReauthorizationGeneration =
                        pendingReauthorizationGeneration,
                };
            }
            else if (pending.TargetRoutePublicationToken != token)
            {
                state =
                    InvalidateFallbackTailYieldTransportEpochReauthorizationState(
                        state,
                        pending,
                        "route_publication_replaced");
                pendingReauthorization = null;
            }
        }

        var next = state with
        {
            CurrentRoutePublicationToken = token,
            LastRoutePublicationGeneration = token.PublicationGeneration,
        };
        if (next.PersistentState.FallbackTailYield.CurrentOperation is
                { } operation &&
            GetEffectiveFallbackTailYieldScope(operation).RoutePublication !=
                token)
        {
            next = InvalidateFallbackTailYieldSubstate(
                next,
                "route_publication_replaced");
        }

        var fallbackTail = next.PersistentState.FallbackTailYield;
        return ApplyFallbackTailYieldSubstate(
            next,
            fallbackTail with
            {
                PendingReauthorization = pendingReauthorization,
                PendingReauthorizationPeerProof =
                    pendingReauthorization ==
                        fallbackTail.PendingReauthorization
                        ? fallbackTail
                            .PendingReauthorizationPeerProof
                        : null,
                RetainedPredecessor =
                    pendingReauthorization is { } boundPending
                    ? boundPending.PredecessorDecision
                    : null,
                LastReauthorizationGeneration = Math.Max(
                    fallbackTail.LastReauthorizationGeneration,
                    pendingReauthorizationGeneration),
            });
    }

    private static FileTransferCoordinatorState ApplyIssuedFallbackAuthority(
        FileTransferCoordinatorState state,
        FileTransferCoordinatorActionKind kind,
        FallbackControlPlaneToken? controlPlaneToken,
        FallbackCheckpointLease? checkpointLease,
        FallbackControlPlanePressureToken? pressureToken,
        FallbackTailYieldDecision? tailYieldDecision,
        FallbackTailYieldTransportEpochReauthorization?
            fallbackTailYieldTransportEpochReauthorization)
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
                CurrentFallbackCheckpointLease = kind == FileTransferCoordinatorActionKind.FallbackTailQuiescent
                    ? null
                    : next.CurrentFallbackCheckpointLease,
            };
            next = ApplyIssuedFallbackTailYieldDecision(
                next,
                yield,
                "fallback_tail_yield_issued",
                fallbackTailYieldTransportEpochReauthorization);
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
                : null,
        };
    }

    internal static FileTransferFallbackTailYieldSubstate
        GetFallbackTailYieldSubstate(FileTransferCoordinatorState state)
        => state.PersistentState.FallbackTailYield;

    internal static FileTransferFallbackTailRetiredOwnerDisposition
        GetFallbackTailRetiredOwnerDisposition(
            FileTransferCoordinatorState state)
    {
        var substate = state.PersistentState.FallbackTailYield;
        FileTransferFallbackTailYieldOperation? retiredOperation = null;
        if (substate.PendingReauthorization is { } pending)
        {
            if (substate.LastTerminalOperation is not
                    { } pendingPredecessor ||
                pendingPredecessor.Decision !=
                    pending.PredecessorDecision)
            {
                return new FileTransferFallbackTailRetiredOwnerDisposition(
                    FileTransferFallbackTailRetiredOwnerDispositionStatus
                        .Unavailable,
                    pending.PredecessorDecision,
                    RetiredScope: null,
                    CurrentScope: null,
                    "fallback_tail_retired_owner_predecessor_scope_missing");
            }

            retiredOperation = pendingPredecessor;
        }
        else if (substate.RetainedPredecessor is { } retained &&
            substate.LastTerminalOperation is { } retainedOperation &&
            retainedOperation.Decision == retained)
        {
            retiredOperation = retainedOperation;
        }
        else if (substate.LastTerminalOperation is { } terminal)
        {
            retiredOperation = terminal;
        }
        else if (substate.CurrentOperation is
            {
                LocalProof: not null,
            } current)
        {
            retiredOperation = current with
            {
                Scope = GetEffectiveFallbackTailYieldScope(current),
            };
        }

        if (retiredOperation is not { } exactRetired)
        {
            return new FileTransferFallbackTailRetiredOwnerDisposition(
                FileTransferFallbackTailRetiredOwnerDispositionStatus
                    .Unavailable,
                Decision: null,
                RetiredScope: null,
                CurrentScope: null,
                "fallback_tail_retired_owner_missing");
        }

        if (!TryCreateCurrentFallbackTailYieldScope(
                state,
                out var currentScope))
        {
            return new FileTransferFallbackTailRetiredOwnerDisposition(
                FileTransferFallbackTailRetiredOwnerDispositionStatus
                    .Unavailable,
                exactRetired.Decision,
                exactRetired.Scope,
                CurrentScope: null,
                "fallback_tail_retired_owner_current_scope_missing");
        }

        var exactScope = exactRetired.Scope == currentScope;
        return new FileTransferFallbackTailRetiredOwnerDisposition(
            exactScope
                ? FileTransferFallbackTailRetiredOwnerDispositionStatus
                    .Suppressed
                : FileTransferFallbackTailRetiredOwnerDispositionStatus
                    .ScopeChanged,
            exactRetired.Decision,
            exactRetired.Scope,
            currentScope,
            exactScope
                ? "fallback_tail_retired_owner_exact_scope"
                : "fallback_tail_retired_owner_scope_changed");
    }

    internal static FileTransferFallbackTailLocalProofReceipt
        CreateFallbackTailLocalProofReceipt(
            FileTransferCoordinatorState state,
            FallbackTailYieldDecision decision,
            string proofIdentity,
            string frameFingerprint,
            long observedUtcMs)
    {
        var operation = state.PersistentState.FallbackTailYield
            .CurrentOperation;
        return operation is { Decision: var current } &&
               current == decision
            ? new FileTransferFallbackTailLocalProofReceipt(
                decision,
                operation.Value.Scope,
                proofIdentity,
                frameFingerprint,
                observedUtcMs)
            : default;
    }

    internal static FileTransferFallbackTailPeerProofReceipt
        CreateFallbackTailPeerProofReceipt(
            FileTransferCoordinatorState state,
            FileTransferFallbackTailPeerProof proof,
            string observationIdentity,
        long observedUtcMs)
    {
        var operation = state.PersistentState.FallbackTailYield
            .CurrentOperation;
        if (operation is { } current)
        {
            return new FileTransferFallbackTailPeerProofReceipt(
                proof,
                current.Scope,
                observationIdentity,
                observedUtcMs);
        }

        return TryCreateCurrentFallbackTailYieldScope(
            state,
            out var observationScope)
            ? new FileTransferFallbackTailPeerProofReceipt(
                proof,
                observationScope,
                observationIdentity,
                observedUtcMs)
            : default;
    }

    internal static FileTransferFallbackTailYieldTransitionDecision
        ApplyFallbackTailLocalProofReceipt(
            FileTransferCoordinatorState state,
            FileTransferFallbackTailLocalProofReceipt receipt)
    {
        var originalSubstate = state.PersistentState.FallbackTailYield;
        state = InvalidateFallbackTailYieldForDataSessionReplacement(state);
        if (!ReferenceEquals(
                originalSubstate,
                state.PersistentState.FallbackTailYield))
        {
            return FallbackTailYieldTransitionInvalidated(
                state,
                "data_session_replaced");
        }

        var substate = state.PersistentState.FallbackTailYield;
        if (substate.CurrentOperation is not { } operation)
        {
            return FallbackTailYieldTransitionRejected(
                state,
                "fallback_tail_yield_operation_missing");
        }

        if (receipt.IsDefault)
        {
            return FallbackTailYieldTransitionRejected(
                state,
                "fallback_tail_local_proof_receipt_invalid");
        }

        if (receipt.Decision != operation.Decision ||
            receipt.Scope != operation.Scope ||
            !receipt.Scope.HasCompleteExecutionScope)
        {
            return FallbackTailYieldTransitionRejected(
                state,
                "fallback_tail_local_proof_receipt_scope_mismatch");
        }

        if (!IsFallbackTailYieldScopeCurrent(
                state,
                GetEffectiveFallbackTailYieldScope(operation),
                out var scopeRejectionReason))
        {
            return FallbackTailYieldTransitionRejected(
                state,
                scopeRejectionReason);
        }

        if (operation.LocalProof is { } existing)
        {
            return existing == receipt
                ? FallbackTailYieldTransition(
                    state,
                    FileTransferFallbackTailYieldTransitionStatus.Idempotent,
                    "fallback_tail_local_proof_receipt_idempotent")
                : FallbackTailYieldTransitionRejected(
                    state,
                    "fallback_tail_local_proof_receipt_conflict");
        }

        operation = operation with
        {
            LocalProof = receipt,
            PairingState = operation.PeerProof is null
                ? FileTransferFallbackTailYieldPairingState.LocalProofObserved
                : operation.PairingState,
        };
        return ApplyFallbackTailProofOperation(
            state,
            substate,
            operation,
            "fallback_tail_local_proof_receipt_applied");
    }

    internal static FileTransferFallbackTailYieldTransitionDecision
        ApplyFallbackTailPeerProofReceipt(
            FileTransferCoordinatorState state,
            FileTransferFallbackTailPeerProofReceipt receipt)
    {
        var originalSubstate = state.PersistentState.FallbackTailYield;
        state = InvalidateFallbackTailYieldForDataSessionReplacement(state);
        if (!ReferenceEquals(
                originalSubstate,
                state.PersistentState.FallbackTailYield))
        {
            return FallbackTailYieldTransitionInvalidated(
                state,
                "data_session_replaced");
        }

        var substate = state.PersistentState.FallbackTailYield;
        if (substate.CurrentOperation is not { } operation)
        {
            return ApplyPendingFallbackTailReauthorizationPeerProofReceipt(
                state,
                substate,
                receipt);
        }

        if (receipt.IsDefault)
        {
            return FallbackTailYieldTransitionRejected(
                state,
                "fallback_tail_peer_proof_receipt_invalid");
        }

        if (receipt.ObservationScope != operation.Scope ||
            !receipt.ObservationScope.HasCompleteExecutionScope)
        {
            return FallbackTailYieldTransitionRejected(
                state,
                "fallback_tail_peer_proof_receipt_scope_mismatch");
        }

        if (!IsFallbackTailYieldScopeCurrent(
                state,
                GetEffectiveFallbackTailYieldScope(operation),
                out var scopeRejectionReason))
        {
            return FallbackTailYieldTransitionRejected(
                state,
                scopeRejectionReason);
        }

        if (!IsFallbackTailPeerProofExactForOperation(
                substate,
                operation,
                receipt.Proof,
                out var proofRejectionReason))
        {
            return FallbackTailYieldTransitionRejected(
                state,
                proofRejectionReason);
        }

        if (operation.PeerProof is { } existing)
        {
            return existing == receipt
                ? FallbackTailYieldTransition(
                    state,
                    FileTransferFallbackTailYieldTransitionStatus.Idempotent,
                    "fallback_tail_peer_proof_receipt_idempotent")
                : FallbackTailYieldTransitionRejected(
                    state,
                    "fallback_tail_peer_proof_receipt_conflict");
        }

        operation = operation with
        {
            PeerProof = receipt,
            PairingState = operation.LocalProof is null
                ? FileTransferFallbackTailYieldPairingState.PeerProofObserved
                : operation.PairingState,
        };
        return ApplyFallbackTailProofOperation(
            state,
            substate,
            operation,
            "fallback_tail_peer_proof_receipt_applied");
    }

    private static FileTransferFallbackTailYieldTransitionDecision
        ApplyPendingFallbackTailReauthorizationPeerProofReceipt(
            FileTransferCoordinatorState state,
            FileTransferFallbackTailYieldSubstate substate,
            FileTransferFallbackTailPeerProofReceipt receipt)
    {
        if (substate.PendingReauthorization is not { } pending)
        {
            return FallbackTailYieldTransitionRejected(
                state,
                "fallback_tail_yield_operation_missing");
        }

        var rejectionReason =
            "fallback_tail_peer_proof_receipt_invalid";
        if (receipt.IsDefault ||
            !TryValidateFallbackTailReauthorizationPeerProof(
                state,
                pending.PredecessorDecision,
                pending,
                receipt.Proof,
                receipt.ObservationScope,
                out rejectionReason))
        {
            return FallbackTailYieldTransitionRejected(
                state,
                receipt.IsDefault
                    ? "fallback_tail_peer_proof_receipt_invalid"
                    : rejectionReason);
        }

        if (substate.PendingReauthorizationPeerProof is { } existing)
        {
            return existing == receipt
                ? FallbackTailYieldTransition(
                    state,
                    FileTransferFallbackTailYieldTransitionStatus.Idempotent,
                    "fallback_tail_reauthorization_peer_proof_receipt_idempotent")
                : FallbackTailYieldTransitionRejected(
                    state,
                    "fallback_tail_reauthorization_peer_proof_receipt_conflict");
        }

        return FallbackTailYieldTransition(
            ApplyFallbackTailYieldSubstate(
                state,
                substate with
                {
                    PendingReauthorizationPeerProof = receipt,
                }),
            FileTransferFallbackTailYieldTransitionStatus.Accepted,
            "fallback_tail_reauthorization_peer_proof_receipt_retained");
    }

    internal static FileTransferFallbackTailYieldTransitionDecision
        CarryForwardFallbackTailPairedReleaseToTransportEpoch(
            FileTransferCoordinatorState state,
            long targetTransportEpoch,
            string reason)
    {
        var normalizedReason = NormalizeReason(reason);
        var substate = state.PersistentState.FallbackTailYield;
        if (substate.CurrentOperation is not
            {
                PairedRelease: { IsDefault: false } release,
                PairingState:
                    FileTransferFallbackTailYieldPairingState.Paired or
                    FileTransferFallbackTailYieldPairingState.ReleaseReserved or
                    FileTransferFallbackTailYieldPairingState.ReleaseConsumed,
            } operation)
        {
            return FallbackTailYieldTransitionRejected(
                state,
                "fallback_tail_paired_release_carry_forward_unavailable");
        }

        if (operation.PairedReleaseCarryForward is { } existing &&
            (existing.IsDefault ||
             existing.Decision != operation.Decision ||
             existing.PairedRelease != release ||
             existing.SourceScope != operation.Scope))
        {
            return FallbackTailYieldTransitionRejected(
                state,
                "fallback_tail_paired_release_carry_forward_state_invalid");
        }

        var sourceScope =
            operation.PairedReleaseCarryForward?.TargetScope ??
            operation.Scope;
        if (!TryCreateFallbackTailPairedReleaseCarryForwardScope(
                state,
                sourceScope,
                targetTransportEpoch,
                out var targetScope,
                out var rejectionReason))
        {
            return FallbackTailYieldTransitionRejected(
                state,
                rejectionReason);
        }

        var receipt =
            new FileTransferFallbackTailPairedReleaseCarryForwardReceipt(
                operation.Decision,
                release,
                operation.Scope,
                targetScope,
                normalizedReason);
        if (operation.PairedReleaseCarryForward is { } current)
        {
            if (current == receipt)
            {
                return FallbackTailYieldTransition(
                    state,
                    FileTransferFallbackTailYieldTransitionStatus.Idempotent,
                    "fallback_tail_paired_release_carry_forward_idempotent");
            }

            if (targetScope.TransportEpoch <=
                current.TargetScope.TransportEpoch)
            {
                return FallbackTailYieldTransitionRejected(
                    state,
                    "fallback_tail_paired_release_carry_forward_conflict");
            }
        }

        var nextOperation = operation with
        {
            PairedReleaseCarryForward = receipt,
        };
        var nextState = ApplyFallbackTailYieldSubstate(
            state,
            substate with { CurrentOperation = nextOperation });
        if (substate.PendingRetryRecheckRequest is { } pendingRequest)
        {
            var compatibilityEvidence =
                CreateFallbackPairedTailYieldCompatibilityEvidence(release);
            if (!TryCreateFallbackPairedTailYieldRuntimeUnlockRetryDispatchRequest(
                    nextState,
                    operation.Decision,
                    compatibilityEvidence,
                    pendingRequest.RetryReason,
                    pendingRequest.RecoveryReason,
                    out var reboundRequest,
                    out var requestRejectionReason))
            {
                return FallbackTailYieldTransitionRejected(
                    state,
                    requestRejectionReason);
            }

            nextState = ApplyFallbackTailYieldSubstate(
                nextState,
                nextState.PersistentState.FallbackTailYield with
                {
                    PendingRetryRecheckRequest = reboundRequest,
                });
        }

        return FallbackTailYieldTransition(
            nextState,
            FileTransferFallbackTailYieldTransitionStatus.Accepted,
            "fallback_tail_paired_release_carried_forward");
    }

    internal static FileTransferFallbackTailYieldTransitionDecision
        InvalidateFallbackTailYieldTransportEpochReauthorization(
            FileTransferCoordinatorState state,
            FallbackTailYieldTransportEpochReauthorization reauthorization,
            string reason)
    {
        var normalizedReason = NormalizeReason(reason);
        var substate = state.PersistentState.FallbackTailYield;
        if (substate.PendingReauthorization is not { } pending)
        {
            return substate.LastInvalidatedReauthorization ==
                       reauthorization &&
                   string.Equals(
                       substate.LastReauthorizationInvalidationReason,
                       normalizedReason,
                       StringComparison.Ordinal)
                ? FallbackTailYieldTransition(
                    state,
                    FileTransferFallbackTailYieldTransitionStatus.Idempotent,
                    "fallback_tail_yield_transport_epoch_reauthorization_invalidation_idempotent")
                : FallbackTailYieldTransitionRejected(
                    state,
                    "fallback_tail_yield_transport_epoch_reauthorization_stale");
        }

        if (reauthorization.IsDefault || pending != reauthorization)
        {
            return FallbackTailYieldTransitionRejected(
                state,
                "fallback_tail_yield_transport_epoch_reauthorization_stale");
        }

        return FallbackTailYieldTransition(
            InvalidateFallbackTailYieldTransportEpochReauthorizationState(
                state,
                pending,
                normalizedReason),
            FileTransferFallbackTailYieldTransitionStatus.Accepted,
            "fallback_tail_yield_transport_epoch_reauthorization_invalidated");
    }

    internal static FileTransferFallbackTailYieldScope
        GetEffectiveFallbackTailYieldScope(
            FileTransferFallbackTailYieldOperation operation)
        => operation.PairedReleaseCarryForward?.TargetScope ??
           operation.Scope;

    internal static FileTransferFallbackTailYieldTransitionDecision
        ReserveFallbackTailPairedRelease(
            FileTransferCoordinatorState state,
            FileTransferFallbackTailPairedReleaseReceipt release,
            RuntimeUnlockRetryDispatchAuthority retryAuthority)
    {
        var originalSubstate = state.PersistentState.FallbackTailYield;
        state = InvalidateFallbackTailYieldForDataSessionReplacement(state);
        if (!ReferenceEquals(
                originalSubstate,
                state.PersistentState.FallbackTailYield))
        {
            return FallbackTailYieldTransitionInvalidated(
                state,
                "data_session_replaced");
        }

        var substate = state.PersistentState.FallbackTailYield;
        if (substate.CurrentOperation is not
            {
                PairedRelease: { } currentRelease,
            } operation ||
            currentRelease != release ||
            release.IsDefault)
        {
            return FallbackTailYieldTransitionRejected(
                state,
                "fallback_tail_paired_release_stale");
        }

        if (!IsFallbackTailYieldScopeCurrent(
                state,
                GetEffectiveFallbackTailYieldScope(operation),
                out var currentnessReason))
        {
            return FallbackTailYieldTransitionRejected(
                state,
                currentnessReason);
        }

        if (!IsFallbackTailRetryAuthorityExactForRelease(
                operation,
                release,
                retryAuthority))
        {
            return FallbackTailYieldTransitionRejected(
                state,
                "fallback_tail_paired_release_retry_authority_mismatch");
        }

        if (substate.CurrentReservation is { } existing)
        {
            return existing.Release == release &&
                   existing.RetryAuthority == retryAuthority
                ? FallbackTailYieldTransition(
                    state,
                    FileTransferFallbackTailYieldTransitionStatus.Idempotent,
                    "fallback_tail_paired_release_reservation_idempotent")
                : FallbackTailYieldTransitionRejected(
                    state,
                    "fallback_tail_paired_release_already_reserved");
        }

        if (substate.LastConsumedReservation is { } consumed &&
            consumed.Release == release)
        {
            return consumed.RetryAuthority == retryAuthority
                ? FallbackTailYieldTransition(
                    state,
                    FileTransferFallbackTailYieldTransitionStatus.Idempotent,
                    "fallback_tail_paired_release_already_consumed")
                : FallbackTailYieldTransitionRejected(
                    state,
                    "fallback_tail_paired_release_consumed_by_other_authority");
        }

        if (substate.PendingRetryRecheckRequest is not
                { } pendingRetryRecheck ||
            !TryValidateRuntimeUnlockRetryDispatchBinding(
                retryAuthority,
                pendingRetryRecheck,
                out _) ||
            pendingRetryRecheck.FallbackPairedTailYieldEvidence !=
                CreateFallbackPairedTailYieldCompatibilityEvidence(
                    release))
        {
            return FallbackTailYieldTransitionRejected(
                state,
                "fallback_tail_paired_release_retry_recheck_stale");
        }

        if (operation.PairingState !=
            FileTransferFallbackTailYieldPairingState.Paired)
        {
            return FallbackTailYieldTransitionRejected(
                state,
                "fallback_tail_paired_release_not_available");
        }

        var reservationGeneration = Math.Max(
            1,
            substate.LastReservationGeneration + 1);
        var reservation =
            new FileTransferFallbackTailPairedReleaseReservation(
                release,
                retryAuthority,
                reservationGeneration);
        var nextOperation = operation with
        {
            PairingState =
                FileTransferFallbackTailYieldPairingState.ReleaseReserved,
        };
        var nextSubstate = substate with
        {
            CurrentOperation = nextOperation,
            CurrentReservation = reservation,
            LastReservationGeneration = reservationGeneration,
        };
        return FallbackTailYieldTransition(
            ApplyFallbackTailYieldSubstate(state, nextSubstate),
            FileTransferFallbackTailYieldTransitionStatus.Accepted,
            "fallback_tail_paired_release_reserved");
    }

    internal static FileTransferFallbackTailYieldTransitionDecision
        ConsumeFallbackTailPairedRelease(
            FileTransferCoordinatorState state,
            FileTransferFallbackTailPairedReleaseReservation reservation,
            RuntimeUnlockRetryDispatchInstallReceipt installReceipt,
            RuntimeUnlockRetryDispatchInstallStatus installStatus)
    {
        var originalSubstate = state.PersistentState.FallbackTailYield;
        state = InvalidateFallbackTailYieldForDataSessionReplacement(state);
        if (!ReferenceEquals(
                originalSubstate,
                state.PersistentState.FallbackTailYield))
        {
            return FallbackTailYieldTransitionInvalidated(
                state,
                "data_session_replaced");
        }

        var substate = state.PersistentState.FallbackTailYield;
        if (substate.LastConsumedReservation is { } consumed &&
            consumed == reservation &&
            substate.LastConsumingInstallReceipt == installReceipt)
        {
            return FallbackTailYieldTransition(
                state,
                FileTransferFallbackTailYieldTransitionStatus.Idempotent,
                "fallback_tail_paired_release_consumption_idempotent");
        }

        if (substate.CurrentOperation is not { } operation ||
            substate.CurrentReservation != reservation)
        {
            return FallbackTailYieldTransitionRejected(
                state,
                "fallback_tail_paired_release_reservation_stale");
        }

        if (!IsFallbackTailYieldScopeCurrent(
                state,
                GetEffectiveFallbackTailYieldScope(operation),
                out var currentnessReason))
        {
            return FallbackTailYieldTransitionRejected(
                state,
                currentnessReason);
        }

        if (installStatus is not
                (RuntimeUnlockRetryDispatchInstallStatus.Installed or
                 RuntimeUnlockRetryDispatchInstallStatus.Idempotent) ||
            installReceipt.IsDefault ||
            installReceipt.Authority != reservation.RetryAuthority)
        {
            return FallbackTailYieldTransitionRejected(
                state,
                "fallback_tail_paired_release_install_receipt_mismatch");
        }

        var nextOperation = operation with
        {
            PairingState =
                FileTransferFallbackTailYieldPairingState.ReleaseConsumed,
        };
        var nextSubstate = substate with
        {
            CurrentOperation = nextOperation,
            PendingRetryRecheckRequest = null,
            CurrentReservation = null,
            LastConsumedReservation = reservation,
            LastConsumingInstallReceipt = installReceipt,
        };
        return FallbackTailYieldTransition(
            ApplyFallbackTailYieldSubstate(state, nextSubstate),
            FileTransferFallbackTailYieldTransitionStatus.Accepted,
            "fallback_tail_paired_release_consumed");
    }

    private static bool IsFallbackTailPairedReleaseConsumedByInstall(
        FileTransferCoordinatorState state,
        RuntimeUnlockRetryDispatchInstallReceipt installReceipt)
    {
        var substate = state.PersistentState.FallbackTailYield;
        return substate.CurrentOperation is
               {
                   PairedRelease: { } release,
                   PairingState:
                       FileTransferFallbackTailYieldPairingState.ReleaseConsumed,
               } &&
               substate.LastConsumedReservation is { } consumed &&
               consumed.Release == release &&
               consumed.RetryAuthority == installReceipt.Authority &&
               substate.LastConsumingInstallReceipt == installReceipt;
    }

    private static FileTransferFallbackTailYieldTransitionDecision
        ApplyFallbackTailProofOperation(
            FileTransferCoordinatorState state,
            FileTransferFallbackTailYieldSubstate substate,
            FileTransferFallbackTailYieldOperation operation,
            string reason)
    {
        var pairingCommitted = false;
        if (operation.PairedRelease is null &&
            operation.LocalProof is { } local &&
            operation.PeerProof is { } peer)
        {
            var pairingGeneration = Math.Max(
                1,
                substate.LastPairingGeneration + 1);
            operation = operation with
            {
                PairingState =
                    FileTransferFallbackTailYieldPairingState.Paired,
                PairedRelease =
                    new FileTransferFallbackTailPairedReleaseReceipt(
                        local,
                        peer,
                        pairingGeneration,
                        RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield,
                        FallbackTailYieldKind.Quiescent.ToString()),
            };
            substate = substate with
            {
                LastPairingGeneration = pairingGeneration,
            };
            pairingCommitted = true;
        }

        var nextState = ApplyFallbackTailYieldSubstate(
            state,
            substate with { CurrentOperation = operation });
        if (pairingCommitted &&
            operation.PairedRelease is not null)
        {
            nextState =
                AttachFallbackTailPairedReleaseRetryRecheckRequest(
                    nextState);
        }

        return FallbackTailYieldTransition(
            nextState,
            FileTransferFallbackTailYieldTransitionStatus.Accepted,
            reason);
    }

    private static FileTransferCoordinatorState
        AttachFallbackTailPairedReleaseRetryRecheckRequest(
            FileTransferCoordinatorState state)
    {
        var substate = state.PersistentState.FallbackTailYield;
        if (substate.PendingRetryRecheckRequest is not null ||
            substate.CurrentOperation is not
            {
                Decision: var decision,
                PairingState:
                    FileTransferFallbackTailYieldPairingState.Paired,
                PairedRelease: { } pairedRelease,
            })
        {
            return state;
        }

        var compatibilityEvidence =
            CreateFallbackPairedTailYieldCompatibilityEvidence(
                pairedRelease);
        if (!TryCreateFallbackPairedTailYieldRuntimeUnlockRetryDispatchRequest(
                state,
                decision,
                compatibilityEvidence,
                "fallback_paired_tail_yield",
                "fallback_tail_pairing_committed",
                out var retryRecheckRequest,
                out _))
        {
            return state;
        }

        return ApplyFallbackTailYieldSubstate(
            state,
            substate with
            {
                PendingRetryRecheckRequest = retryRecheckRequest,
            });
    }

    private static bool IsFallbackTailPeerProofExactForOperation(
        FileTransferFallbackTailYieldSubstate substate,
        FileTransferFallbackTailYieldOperation operation,
        FileTransferFallbackTailPeerProof proof,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        var scope = operation.Scope;
        if (proof.IsDefault)
        {
            rejectionReason = "fallback_tail_peer_proof_invalid";
            return false;
        }

        if (!FileTransferAuthorityIdParser.TryNormalizeFallbackTailYield(
                proof.AuthorityId.ToString(),
                out _) ||
            proof.AuthorityId == operation.Decision.AuthorityId ||
            (proof.PredecessorAuthorityId is { IsDefault: false } predecessor &&
             (!FileTransferAuthorityIdParser.TryNormalizeFallbackTailYield(
                  predecessor.ToString(),
                  out _) ||
              predecessor == proof.AuthorityId)))
        {
            rejectionReason = "fallback_tail_peer_proof_authority_invalid";
            return false;
        }

        var expectedOwnerKey = BuildFallbackTailYieldOwnerKey(
            scope.SessionId,
            scope.TransferId,
            scope.RouteToken,
            scope.ProtocolVersion,
            proof.LiveRouteEpoch,
            proof.LegGeneration,
            proof.BridgeRecoveryGeneration,
            proof.TransportEpoch,
            proof.CheckpointRequestIdSnapshot);
        var expectedPairKey = BuildFallbackTailYieldPairKey(
            scope.SessionId,
            scope.TransferId,
            scope.RouteToken,
            scope.ProtocolVersion,
            proof.LiveRouteEpoch,
            proof.TransportEpoch,
            proof.CheckpointRequestIdSnapshot);
        if (!string.Equals(
                proof.SessionId,
                scope.SessionId,
                StringComparison.Ordinal) ||
            !string.Equals(
                proof.TransferId,
                scope.TransferId,
                StringComparison.Ordinal) ||
            !AreFallbackTailProofDirectionsOpposite(
                scope.Direction,
                proof.Direction) ||
            proof.LiveRouteEpoch != scope.LiveRouteEpoch ||
            proof.TransportEpoch != scope.TransportEpoch ||
            !string.Equals(
                proof.OwnerKey,
                expectedOwnerKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                proof.PairKey,
                expectedPairKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                proof.PairKey,
                scope.PairKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                proof.CheckpointRequestIdSnapshot,
                scope.CheckpointRequestIdSnapshot,
                StringComparison.Ordinal))
        {
            rejectionReason = "fallback_tail_peer_proof_scope_mismatch";
            return false;
        }

        if (!TryGetExactFallbackTailPeerPredecessorProof(
                substate,
                operation,
                out var predecessorProof,
                out var operationIsSuccessor,
                out rejectionReason))
        {
            return false;
        }

        if (operationIsSuccessor)
        {
            if (predecessorProof is null)
            {
                rejectionReason =
                    "fallback_tail_peer_proof_predecessor_receipt_missing";
                return false;
            }

            if (proof.AuthorityId ==
                predecessorProof.Value.AuthorityId)
            {
                rejectionReason =
                    "fallback_tail_peer_proof_authority_tuple_mutated";
                return false;
            }

            if (proof.PredecessorAuthorityId !=
                    predecessorProof.Value.AuthorityId ||
                proof.YieldGeneration <=
                    predecessorProof.Value.YieldGeneration)
            {
                rejectionReason =
                    "fallback_tail_peer_proof_successor_lineage_invalid";
                return false;
            }
        }
        else
        {
            if (proof.PredecessorAuthorityId is not null)
            {
                rejectionReason =
                    "fallback_tail_peer_proof_successor_lineage_invalid";
                return false;
            }
        }

        return true;
    }

    private static bool TryGetExactFallbackTailPeerPredecessorProof(
        FileTransferFallbackTailYieldSubstate substate,
        FileTransferFallbackTailYieldOperation operation,
        out FileTransferFallbackTailPeerProof? predecessorProof,
        out bool operationIsSuccessor,
        out string rejectionReason)
    {
        predecessorProof = null;
        rejectionReason = "accepted";
        var sourceReauthorization =
            operation.SourceReauthorization?.Reauthorization;
        var predecessorAuthorityId =
            operation.Decision.PredecessorAuthorityId;
        operationIsSuccessor =
            sourceReauthorization is not null ||
            predecessorAuthorityId is { IsDefault: false };
        if (!operationIsSuccessor)
        {
            return true;
        }

        if (predecessorAuthorityId is not { IsDefault: false })
        {
            rejectionReason =
                "fallback_tail_peer_proof_successor_operation_lineage_invalid";
            return false;
        }

        FallbackTailYieldDecision? expectedPredecessor = null;
        if (sourceReauthorization is { } source)
        {
            if (source.IsDefault ||
                source.PredecessorDecision.AuthorityId !=
                    predecessorAuthorityId)
            {
                rejectionReason =
                    "fallback_tail_peer_proof_successor_operation_lineage_invalid";
                return false;
            }

            expectedPredecessor = source.PredecessorDecision;
        }
        else if (substate.RetainedPredecessor is { } retained &&
                 retained.AuthorityId == predecessorAuthorityId)
        {
            expectedPredecessor = retained;
        }

        if (substate.LastTerminalOperation is not { } terminal ||
            terminal.Decision.AuthorityId != predecessorAuthorityId ||
            expectedPredecessor is { } exact &&
            terminal.Decision != exact)
        {
            return true;
        }

        predecessorProof = terminal.PeerProof?.Proof;
        return true;
    }

    private static bool AreFallbackTailProofDirectionsOpposite(
        FileTransferDirection localDirection,
        FileTransferDirection peerDirection)
        => (localDirection == FileTransferDirection.Outbound &&
            peerDirection == FileTransferDirection.Inbound) ||
           (localDirection == FileTransferDirection.Inbound &&
            peerDirection == FileTransferDirection.Outbound);

    private static bool IsFallbackTailRetryAuthorityExactForRelease(
        FileTransferFallbackTailYieldOperation operation,
        FileTransferFallbackTailPairedReleaseReceipt release,
        RuntimeUnlockRetryDispatchAuthority authority)
    {
        var scope = GetEffectiveFallbackTailYieldScope(operation);
        return !authority.IsDefault &&
               string.Equals(
                   authority.SessionId,
                   scope.SessionId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   authority.TransferId,
                   scope.TransferId,
                   StringComparison.Ordinal) &&
               authority.Direction == scope.Direction &&
               authority.RuntimeOwner == scope.RuntimeOwner &&
               authority.SourcePublicationAuthority ==
                   scope.RoutePublication &&
               authority.Route == scope.Route &&
               string.Equals(
                   authority.RouteToken,
                   scope.RouteToken,
                   StringComparison.Ordinal) &&
               authority.ProtocolVersion == scope.ProtocolVersion &&
               authority.LiveRouteEpoch == scope.LiveRouteEpoch &&
               authority.TerminalGeneration == scope.TerminalGeneration &&
               authority.LegGeneration == scope.LegGeneration &&
               authority.TransportEpoch == scope.TransportEpoch &&
               authority.BridgeRecoveryGeneration ==
                   scope.BridgeRecoveryGeneration &&
               authority.ReleaseKind ==
                   RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield &&
               authority.ReleaseAuthorityId == release.ReleaseAuthorityId &&
               authority.ReleaseGeneration == release.ReleaseGeneration &&
               string.Equals(
                   authority.ReleaseOutcome,
                   release.ReleaseOutcome,
                   StringComparison.Ordinal);
    }

    private static FileTransferFallbackTailYieldTransitionDecision
        FallbackTailYieldTransitionRejected(
            FileTransferCoordinatorState state,
            string reason)
        => FallbackTailYieldTransition(
            state,
            FileTransferFallbackTailYieldTransitionStatus.Rejected,
            reason);

    private static FileTransferFallbackTailYieldTransitionDecision
        FallbackTailYieldTransitionInvalidated(
            FileTransferCoordinatorState state,
            string reason)
        => FallbackTailYieldTransition(
            state,
            FileTransferFallbackTailYieldTransitionStatus.Invalidated,
            reason);

    private static FileTransferFallbackTailYieldTransitionDecision
        FallbackTailYieldTransition(
            FileTransferCoordinatorState state,
            FileTransferFallbackTailYieldTransitionStatus status,
            string reason)
        => new(status, NormalizeReason(reason), state);

    private static FallbackTailYieldDecision?
        GetCurrentFallbackTailYieldDecision(FileTransferCoordinatorState state)
        => state.PersistentState.FallbackTailYield.CurrentOperation?.Decision;

    private static FallbackTailYieldTransportEpochReauthorization?
        GetPendingFallbackTailYieldTransportEpochReauthorization(
            FileTransferCoordinatorState state)
        => state.PersistentState.FallbackTailYield.PendingReauthorization;

    private static bool TryGetFallbackTailTransportEpochAdoptionDecision(
        FileTransferCoordinatorState state,
        out TransportEpochDecision decision,
        out string rejectionReason)
    {
        decision = default;
        rejectionReason = "accepted";
        if (state.CurrentTransportEpochDecision is not { } current ||
            current.IsDefault ||
            !current.Accepted ||
            current.ActionKind != TransportEpochActionKind.Adopt ||
            current.TargetRoute != FileTransferRoute.PostTunaFallbackV6 ||
            current.TargetProtocolVersion !=
                FileTransferProtocol.ProtocolVersionV6 ||
            current.TargetTransport != FileTransferTransportKind.RegularNkn)
        {
            rejectionReason =
                "fallback_tail_yield_transport_epoch_decision_missing";
            return false;
        }

        if (!IsTransportEpochDecisionCurrent(
                state,
                current,
                out rejectionReason) &&
            !IsFallbackTailTransportEpochAdoptionApplied(
                state,
                current,
                out rejectionReason))
        {
            return false;
        }

        decision = current;
        return true;
    }

    private static bool IsFallbackTailTransportEpochAdoptionApplied(
        FileTransferCoordinatorState state,
        TransportEpochDecision decision,
        out string rejectionReason)
    {
        rejectionReason = "current";
        if (state.CurrentTransportEpochDecision != decision ||
            decision.IsDefault ||
            !decision.Accepted ||
            decision.ActionKind != TransportEpochActionKind.Adopt ||
            decision.TargetRoute !=
                FileTransferRoute.PostTunaFallbackV6 ||
            decision.TargetProtocolVersion !=
                FileTransferProtocol.ProtocolVersionV6 ||
            decision.TargetTransport !=
                FileTransferTransportKind.RegularNkn ||
            decision.TargetTransportEpoch <= decision.TransportEpoch ||
            state.CurrentRuntimeOwner is not { } owner ||
            state.CurrentLeg is not { } leg ||
            !IsRuntimeOwnerCurrent(state, decision.RuntimeOwner, out _) ||
            decision.RuntimeOwner != owner ||
            decision.CurrentRoute != state.RouteSelection.Route ||
            decision.CurrentProtocolVersion !=
                state.RouteSelection.ProtocolVersion ||
            decision.CurrentLiveRouteEpoch !=
                (state.CurrentLiveRouteEpoch?.EpochId ??
                 leg.LiveRouteEpochId) ||
            decision.CurrentRuntimeGeneration !=
                owner.RuntimeGeneration ||
            decision.CurrentTerminalGeneration !=
                state.TerminalGeneration ||
            decision.CurrentLegGeneration != leg.Generation ||
            leg.TransportEpochId != decision.TargetTransportEpoch ||
            leg.BridgeRecoveryGeneration <
                decision.BridgeRecoveryGeneration)
        {
            rejectionReason =
                "fallback_tail_transport_epoch_adoption_not_current";
            return false;
        }

        return true;
    }

    internal static bool
        TryCreateFallbackTailYieldTransportEpochReauthorizationRequest(
            FileTransferCoordinatorState state,
            long targetTransportEpoch,
            string reason,
            out FallbackTailYieldTransportEpochReauthorizationRequest request,
            out string rejectionReason)
    {
        request = default;
        rejectionReason = "accepted";
        if (state.IsTerminal)
        {
            rejectionReason = "transfer_terminal";
            return false;
        }

        var substate = state.PersistentState.FallbackTailYield;
        if (substate.PendingReauthorization is { } pending)
        {
            if (targetTransportEpoch < pending.TargetTransportEpoch)
            {
                rejectionReason =
                    "fallback_tail_yield_transport_epoch_reauthorization_target_stale";
                return false;
            }

            var targetDecision =
                state.CurrentTransportEpochDecision is { } currentDecision
                    ? currentDecision
                    : pending.TargetTransportEpochDecision;
            if (state.CurrentTransportEpochDecision is { } &&
                !TryGetFallbackTailTransportEpochAdoptionDecision(
                    state,
                    out targetDecision,
                    out rejectionReason))
            {
                return false;
            }

            if (targetDecision.TargetTransportEpoch !=
                targetTransportEpoch)
            {
                rejectionReason =
                    "fallback_tail_yield_transport_epoch_decision_target_mismatch";
                return false;
            }

            if (!IsFallbackTailYieldTransportEpochReauthorizationCurrent(
                    state,
                    pending,
                    allowTransportEpochDecisionReplacement:
                        targetDecision !=
                        pending.TargetTransportEpochDecision,
                    out rejectionReason))
            {
                return false;
            }

            if (HasFallbackTailPairedReleaseForDecision(
                    state,
                    pending.PredecessorDecision))
            {
                rejectionReason =
                    "fallback_tail_yield_transport_epoch_reauthorization_already_paired";
                return false;
            }

            request =
                new FallbackTailYieldTransportEpochReauthorizationRequest(
                    pending.PredecessorDecision,
                    pending.SourceRuntimeOwner,
                    pending.SourceRoutePublicationToken,
                    pending.SourceTransportEpoch,
                    targetTransportEpoch,
                    targetDecision,
                    targetTransportEpoch == pending.TargetTransportEpoch
                        ? pending.Reason
                        : NormalizeReason(reason))
                {
                    PendingReauthorization = pending,
                };
            return true;
        }

        if (substate.CurrentOperation is not
            {
                Decision:
                {
                    Accepted: true,
                    YieldKind: FallbackTailYieldKind.Quiescent,
                } predecessor,
                PairedRelease: null,
            } operation)
        {
            rejectionReason =
                "fallback_tail_yield_transport_epoch_reauthorization_predecessor_missing";
            return false;
        }

        if (state.CurrentRuntimeOwner is not { } sourceOwner ||
            state.CurrentRoutePublicationToken is not
                { IsDefault: false } sourcePublication ||
            state.CurrentLeg is not { } sourceLeg ||
            !TryGetFallbackTailTransportEpochAdoptionDecision(
                state,
                out var targetTransportEpochDecision,
                out rejectionReason))
        {
            if (string.Equals(
                    rejectionReason,
                    "accepted",
                    StringComparison.Ordinal))
            {
                rejectionReason =
                    "fallback_tail_yield_transport_epoch_reauthorization_source_missing";
            }
            return false;
        }

        if (targetTransportEpochDecision.TargetTransportEpoch !=
            targetTransportEpoch)
        {
            rejectionReason =
                "fallback_tail_yield_transport_epoch_decision_target_mismatch";
            return false;
        }

        var scope = operation.Scope;
        var currentLiveRouteEpoch =
            state.CurrentLiveRouteEpoch?.EpochId ??
            sourceLeg.LiveRouteEpochId;
        var checkpointSnapshot =
            NormalizeFallbackTailYieldCheckpointRequestId(
                state.CurrentFallbackCheckpointLease);
        var sourceEpochAlreadyAdopted =
            sourceLeg.TransportEpochId == targetTransportEpoch;
        if (targetTransportEpoch <= scope.TransportEpoch ||
            state.RouteSelection.Route !=
                FileTransferRoute.PostTunaFallbackV6 ||
            state.RouteSelection.ProtocolVersion !=
                FileTransferProtocol.ProtocolVersionV6 ||
            !IsCurrentPostTunaFallbackLeg(sourceLeg) ||
            sourceOwner != scope.RuntimeOwner ||
            sourcePublication != scope.RoutePublication ||
            !IsRoutePublicationTokenCurrent(
                state,
                sourcePublication,
                out _) ||
            !string.Equals(
                state.SessionId,
                scope.SessionId,
                StringComparison.Ordinal) ||
            !string.Equals(
                state.TransferId,
                scope.TransferId,
                StringComparison.Ordinal) ||
            state.Direction != scope.Direction ||
            state.RouteSelection.Route != scope.Route ||
            !string.Equals(
                state.RouteSelection.TelemetryToken,
                scope.RouteToken,
                StringComparison.Ordinal) ||
            state.RouteSelection.ProtocolVersion !=
                scope.ProtocolVersion ||
            currentLiveRouteEpoch != scope.LiveRouteEpoch ||
            state.TerminalGeneration != scope.TerminalGeneration ||
            sourceLeg.Generation != scope.LegGeneration ||
            (sourceLeg.TransportEpochId != scope.TransportEpoch &&
             !sourceEpochAlreadyAdopted) ||
            (!sourceEpochAlreadyAdopted &&
             sourceLeg.BridgeRecoveryGeneration !=
                 scope.BridgeRecoveryGeneration) ||
            (sourceEpochAlreadyAdopted &&
             sourceLeg.BridgeRecoveryGeneration <
                 scope.BridgeRecoveryGeneration) ||
            !string.Equals(
                checkpointSnapshot,
                scope.CheckpointRequestIdSnapshot,
                StringComparison.Ordinal) ||
            sourceLeg.CheckpointGeneration !=
                scope.CheckpointGeneration ||
            !string.Equals(
                state.CurrentDataSessionIdentity,
                scope.DataSessionIdentity,
                StringComparison.Ordinal) ||
            state.CurrentDataSessionGeneration !=
                scope.DataSessionGeneration ||
            predecessor.CheckpointLease !=
                state.CurrentFallbackCheckpointLease ||
            !string.Equals(
                predecessor.OwnerKey,
                scope.OwnerKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                predecessor.PairKey,
                scope.PairKey,
                StringComparison.Ordinal))
        {
            rejectionReason =
                "fallback_tail_yield_transport_epoch_reauthorization_source_mismatch";
            return false;
        }

        request =
            new FallbackTailYieldTransportEpochReauthorizationRequest(
                predecessor,
                sourceOwner,
                sourcePublication,
                scope.TransportEpoch,
                targetTransportEpoch,
                targetTransportEpochDecision,
                NormalizeReason(reason));
        return true;
    }

    internal static bool
        TryCreateFallbackTailYieldTransportEpochReauthorizationRequestFromPeerProof(
            FileTransferCoordinatorState state,
            FileTransferFallbackTailPeerProof proof,
            string reason,
            out FallbackTailYieldTransportEpochReauthorizationRequest request,
            out string rejectionReason)
    {
        request = default;
        rejectionReason = "accepted";
        if (proof.IsDefault)
        {
            rejectionReason =
                "fallback_tail_reauthorization_peer_proof_invalid";
            return false;
        }

        if (!TryCreateFallbackTailYieldTransportEpochReauthorizationRequest(
                state,
                proof.TransportEpoch,
                reason,
                out request,
                out rejectionReason) ||
            !TryCreateCurrentFallbackTailYieldScope(
                state,
                out var targetScope))
        {
            return false;
        }

        return TryValidateFallbackTailReauthorizationPeerProof(
            state,
            request.PredecessorDecision,
            request.PendingReauthorization,
            proof,
            targetScope,
            out rejectionReason);
    }

    private static bool TryValidateFallbackTailReauthorizationPeerProof(
        FileTransferCoordinatorState state,
        FallbackTailYieldDecision predecessorDecision,
        FallbackTailYieldTransportEpochReauthorization?
            pendingReauthorization,
        FileTransferFallbackTailPeerProof proof,
        FileTransferFallbackTailYieldScope targetScope,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        var substate = state.PersistentState.FallbackTailYield;
        if (predecessorDecision.IsDefault ||
            proof.IsDefault ||
            !targetScope.HasCompleteExecutionScope ||
            !TryCreateCurrentFallbackTailYieldScope(
                state,
                out var currentTargetScope) ||
            currentTargetScope != targetScope ||
            pendingReauthorization is { } pending &&
            (substate.PendingReauthorization != pending ||
             pending.PredecessorDecision != predecessorDecision ||
             targetScope.RuntimeOwner != pending.TargetRuntimeOwner ||
             targetScope.RoutePublication !=
                 pending.TargetRoutePublicationToken ||
             targetScope.TerminalGeneration !=
                 pending.TargetTerminalGeneration ||
             targetScope.LegGeneration != pending.TargetLegGeneration ||
             targetScope.TransportEpoch != pending.TargetTransportEpoch ||
             targetScope.BridgeRecoveryGeneration !=
                 pending.TargetBridgeRecoveryGeneration ||
             !string.Equals(
                 targetScope.CheckpointRequestIdSnapshot,
                 pending.TargetCheckpointRequestIdSnapshot,
                 StringComparison.Ordinal) ||
             targetScope.CheckpointGeneration !=
                 pending.TargetCheckpointGeneration ||
             !string.Equals(
                 targetScope.DataSessionIdentity,
                 pending.TargetDataSessionIdentity,
                 StringComparison.Ordinal) ||
             targetScope.DataSessionGeneration !=
                 pending.TargetDataSessionGeneration))
        {
            rejectionReason =
                "fallback_tail_reauthorization_peer_proof_target_scope_mismatch";
            return false;
        }

        FileTransferFallbackTailYieldOperation? predecessorOperation =
            substate.CurrentOperation is { } current &&
            current.Decision == predecessorDecision
                ? current
                : substate.LastTerminalOperation is { } terminal &&
                  terminal.Decision == predecessorDecision
                    ? terminal
                    : null;
        if (predecessorOperation is not
            {
                PeerProof: { Proof: var predecessorPeerProof },
            })
        {
            rejectionReason =
                "fallback_tail_reauthorization_peer_predecessor_receipt_missing";
            return false;
        }

        var expectedOwnerKey = BuildFallbackTailYieldOwnerKey(
            targetScope.SessionId,
            targetScope.TransferId,
            targetScope.RouteToken,
            targetScope.ProtocolVersion,
            targetScope.LiveRouteEpoch,
            proof.LegGeneration,
            proof.BridgeRecoveryGeneration,
            targetScope.TransportEpoch,
            targetScope.CheckpointRequestIdSnapshot);
        if (!FileTransferAuthorityIdParser.TryNormalizeFallbackTailYield(
                proof.AuthorityId.ToString(),
                out _) ||
            proof.AuthorityId == predecessorPeerProof.AuthorityId ||
            proof.PredecessorAuthorityId !=
                predecessorPeerProof.AuthorityId ||
            proof.YieldGeneration <=
                predecessorPeerProof.YieldGeneration ||
            !string.Equals(
                proof.SessionId,
                targetScope.SessionId,
                StringComparison.Ordinal) ||
            !string.Equals(
                proof.TransferId,
                targetScope.TransferId,
                StringComparison.Ordinal) ||
            !AreFallbackTailProofDirectionsOpposite(
                targetScope.Direction,
                proof.Direction) ||
            proof.LiveRouteEpoch != targetScope.LiveRouteEpoch ||
            proof.TransportEpoch != targetScope.TransportEpoch ||
            !string.Equals(
                proof.CheckpointRequestIdSnapshot,
                targetScope.CheckpointRequestIdSnapshot,
                StringComparison.Ordinal) ||
            !string.Equals(
                proof.PairKey,
                targetScope.PairKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                proof.OwnerKey,
                expectedOwnerKey,
                StringComparison.Ordinal))
        {
            rejectionReason =
                "fallback_tail_reauthorization_peer_proof_lineage_mismatch";
            return false;
        }

        return true;
    }

    private static int GetLastFallbackTailYieldGeneration(
        FileTransferCoordinatorState state)
        => state.PersistentState.FallbackTailYield.LastYieldGeneration;

    private static int
        GetLastFallbackTailYieldTransportEpochReauthorizationGeneration(
            FileTransferCoordinatorState state)
        => state.PersistentState.FallbackTailYield
            .LastReauthorizationGeneration;

    private static FileTransferCoordinatorState ApplyFallbackTailYieldSubstate(
        FileTransferCoordinatorState state,
        FileTransferFallbackTailYieldSubstate substate)
        => state with
        {
            PersistentState = state.PersistentState with
            {
                FallbackTailYield = substate,
            },
        };

    private static FileTransferCoordinatorState
        InvalidateFallbackTailYieldSubstate(
        FileTransferCoordinatorState state,
        string reason)
    {
        var substate = state.PersistentState.FallbackTailYield;
        var normalizedReason = NormalizeReason(reason);
        var lastTerminal = substate.LastTerminalOperation;
        if (substate.CurrentOperation is { } current)
        {
            lastTerminal = current with
            {
                PairingState =
                    FileTransferFallbackTailYieldPairingState.Invalidated,
                InvalidationReason = normalizedReason,
            };
        }

        return ApplyFallbackTailYieldSubstate(
            state,
            substate with
            {
                CurrentOperation = null,
                RetainedPredecessor = null,
                PendingReauthorization = null,
                PendingReauthorizationPeerProof = null,
                PendingRetryRecheckRequest = null,
                CurrentReservation = null,
                LastTerminalOperation = lastTerminal,
                LastInvalidatedReauthorization =
                    substate.PendingReauthorization ??
                    substate.LastInvalidatedReauthorization,
                LastReauthorizationInvalidationReason =
                    substate.PendingReauthorization is not null
                        ? normalizedReason
                        : substate.LastReauthorizationInvalidationReason,
            });
    }

    private static FileTransferCoordinatorState
        InvalidateFallbackTailYieldTransportEpochReauthorizationState(
            FileTransferCoordinatorState state,
            FallbackTailYieldTransportEpochReauthorization reauthorization,
            string reason)
    {
        var substate = state.PersistentState.FallbackTailYield;
        if (substate.PendingReauthorization != reauthorization)
        {
            return state;
        }

        return ApplyFallbackTailYieldSubstate(
            state,
            substate with
            {
                PendingReauthorization = null,
                PendingReauthorizationPeerProof = null,
                RetainedPredecessor =
                    substate.RetainedPredecessor ==
                    reauthorization.PredecessorDecision
                        ? null
                        : substate.RetainedPredecessor,
                LastInvalidatedReauthorization = reauthorization,
                LastReauthorizationInvalidationReason =
                    NormalizeReason(reason),
            });
    }

    private static FileTransferCoordinatorState
        InvalidateFallbackTailYieldForDataSessionReplacement(
            FileTransferCoordinatorState state)
    {
        var fallbackTail = state.PersistentState.FallbackTailYield;
        var operationDataSessionChanged =
            fallbackTail.CurrentOperation is { } operation &&
            GetEffectiveFallbackTailYieldScope(operation) is var operationScope &&
            (!string.Equals(
                 operationScope.DataSessionIdentity,
                 state.CurrentDataSessionIdentity,
                 StringComparison.Ordinal) ||
             operationScope.DataSessionGeneration !=
                 state.CurrentDataSessionGeneration);
        var pendingDataSessionChanged =
            fallbackTail.PendingReauthorization is { } pending &&
            (!string.Equals(
                 pending.TargetDataSessionIdentity,
                 state.CurrentDataSessionIdentity,
                 StringComparison.Ordinal) ||
             pending.TargetDataSessionGeneration !=
                 state.CurrentDataSessionGeneration);
        return operationDataSessionChanged || pendingDataSessionChanged
            ? InvalidateFallbackTailYieldSubstate(
                state,
                "data_session_replaced")
            : state;
    }

    private static FileTransferCoordinatorState
        RebindPendingFallbackTailYieldCheckpointScope(
            FileTransferCoordinatorState state,
            string reason)
    {
        var substate = state.PersistentState.FallbackTailYield;
        if (substate.CurrentOperation is not null)
        {
            return InvalidateFallbackTailYieldSubstate(
                state,
                reason);
        }

        if (substate.PendingReauthorization is not { } pending)
        {
            return state;
        }

        if (state.IsTerminal ||
            state.CurrentRuntimeOwner is not { } owner ||
            state.CurrentLeg is not { } leg ||
            owner != pending.TargetRuntimeOwner ||
            state.TerminalGeneration !=
                pending.TargetTerminalGeneration ||
            leg.Generation != pending.TargetLegGeneration ||
            leg.TransportEpochId != pending.TargetTransportEpoch ||
            leg.BridgeRecoveryGeneration !=
                pending.TargetBridgeRecoveryGeneration ||
            state.CurrentRoutePublicationToken !=
                pending.TargetRoutePublicationToken ||
            (state.CurrentTransportEpochDecision is { } transportDecision &&
             transportDecision !=
                 pending.TargetTransportEpochDecision) ||
            !string.Equals(
                state.CurrentDataSessionIdentity,
                pending.TargetDataSessionIdentity,
                StringComparison.Ordinal) ||
            state.CurrentDataSessionGeneration !=
                pending.TargetDataSessionGeneration)
        {
            var invalidated = substate with
            {
                PendingReauthorization = null,
                PendingReauthorizationPeerProof = null,
                RetainedPredecessor = null,
                LastInvalidatedReauthorization = pending,
                LastReauthorizationInvalidationReason =
                    NormalizeReason(reason),
            };
            return ApplyFallbackTailYieldSubstate(state, invalidated);
        }

        var checkpointRequestId =
            NormalizeFallbackTailYieldCheckpointRequestId(
                state.CurrentFallbackCheckpointLease);
        var checkpointGeneration = leg.CheckpointGeneration;
        var checkpointLease = state.CurrentFallbackCheckpointLease;
        if (string.Equals(
                pending.TargetCheckpointRequestIdSnapshot,
                checkpointRequestId,
                StringComparison.Ordinal) &&
            pending.TargetCheckpointGeneration == checkpointGeneration &&
            pending.TargetCheckpointLease == checkpointLease)
        {
            return state;
        }

        var reauthorizationGeneration = Math.Max(
            pending.ReauthorizationGeneration + 1,
            substate.LastReauthorizationGeneration + 1);
        var rebound = pending with
        {
            TargetCheckpointRequestIdSnapshot = checkpointRequestId,
            TargetCheckpointGeneration = checkpointGeneration,
            TargetCheckpointLease = checkpointLease,
            TargetBridgeRecoveryDecision =
                state.CurrentBridgeRecoveryDecision,
            ReauthorizationGeneration = reauthorizationGeneration,
            Reason = NormalizeReason(reason),
        };
        return ApplyFallbackTailYieldSubstate(
            state,
            substate with
            {
                PendingReauthorization = rebound,
                PendingReauthorizationPeerProof = null,
                RetainedPredecessor = rebound.PredecessorDecision,
                LastReauthorizationGeneration =
                    reauthorizationGeneration,
            });
    }

    private static FileTransferCoordinatorState
        ApplyIssuedFallbackTailYieldDecision(
            FileTransferCoordinatorState state,
            FallbackTailYieldDecision decision,
            string reason,
            FallbackTailYieldTransportEpochReauthorization?
                sourceReauthorization = null)
    {
        var substate = state.PersistentState.FallbackTailYield;
        if (substate.CurrentOperation is { } existing &&
            existing.Decision == decision)
        {
            return state;
        }

        var retainedPredecessor = substate.RetainedPredecessor;
        var lastTerminal = substate.LastTerminalOperation;
        if (substate.CurrentOperation is { } current &&
            decision.PredecessorAuthorityId ==
                current.Decision.AuthorityId)
        {
            retainedPredecessor = current.Decision;
            lastTerminal = current with
            {
                PairingState =
                    FileTransferFallbackTailYieldPairingState.Invalidated,
                InvalidationReason = NormalizeReason(reason),
            };
        }
        else if (substate.PendingReauthorization is { } pending &&
                 decision.PredecessorAuthorityId ==
                     pending.PredecessorDecision.AuthorityId)
        {
            retainedPredecessor = pending.PredecessorDecision;
        }

        var scope = CreateFallbackTailYieldScope(state, decision);
        if (!scope.HasCompleteExecutionScope)
        {
            return state;
        }

        var pendingPeerProof =
            sourceReauthorization is { } issuedReauthorization &&
            substate.PendingReauthorization ==
                issuedReauthorization
                ? substate.PendingReauthorizationPeerProof
                : null;
        return ApplyFallbackTailYieldSubstate(
            state,
            substate with
            {
                CurrentOperation =
                    new FileTransferFallbackTailYieldOperation(
                        decision,
                        scope,
                        pendingPeerProof is null
                            ? FileTransferFallbackTailYieldPairingState
                                .YieldIssued
                            : FileTransferFallbackTailYieldPairingState
                                .PeerProofObserved,
                        PeerProof: pendingPeerProof,
                        SourceReauthorization:
                            sourceReauthorization is
                                { } exactReauthorization
                                ? new FileTransferFallbackTailYieldReauthorizationReceipt(
                                    exactReauthorization)
                                : null),
                RetainedPredecessor = retainedPredecessor,
                PendingReauthorization = null,
                PendingReauthorizationPeerProof = null,
                CurrentReservation = null,
                LastTerminalOperation = lastTerminal,
                LastYieldGeneration = Math.Max(
                    substate.LastYieldGeneration,
                    decision.YieldGeneration),
            });
    }

    private static bool TryGetExactCurrentFallbackTailYieldDecision(
        FileTransferCoordinatorState state,
        FallbackTailYieldKind yieldKind,
        FileTransferAuthorityId? predecessorAuthorityId,
        FallbackTailYieldTransportEpochReauthorization?
            sourceReauthorization,
        out FallbackTailYieldDecision decision)
    {
        if (state.PersistentState.FallbackTailYield.CurrentOperation is
                { } operation &&
            operation.Decision.Accepted &&
            operation.Decision.YieldKind == yieldKind &&
            operation.Decision.PredecessorAuthorityId ==
                predecessorAuthorityId &&
            operation.SourceReauthorization ==
                (sourceReauthorization is { } exactReauthorization
                    ? new FileTransferFallbackTailYieldReauthorizationReceipt(
                        exactReauthorization)
                    : null) &&
            IsFallbackTailYieldScopeCurrent(
                state,
                GetEffectiveFallbackTailYieldScope(operation),
                out _))
        {
            decision = operation.Decision;
            return true;
        }

        decision = default;
        return false;
    }

    private static bool HasCurrentFallbackTailPairedRelease(
        FileTransferCoordinatorState state)
        => state.PersistentState.FallbackTailYield.CurrentOperation is
               {
                   PairedRelease: { IsDefault: false },
                   PairingState:
                       FileTransferFallbackTailYieldPairingState.Paired or
                       FileTransferFallbackTailYieldPairingState
                           .ReleaseReserved or
                       FileTransferFallbackTailYieldPairingState
                           .ReleaseConsumed,
               } operation &&
           IsFallbackTailYieldDecisionCurrent(
               state,
               operation.Decision,
               out _);

    private static bool HasFallbackTailPairedReleaseForDecision(
        FileTransferCoordinatorState state,
        FallbackTailYieldDecision decision)
    {
        var fallbackTail = state.PersistentState.FallbackTailYield;
        var operation =
            fallbackTail.CurrentOperation is { } current &&
            current.Decision == decision
                ? current
                : fallbackTail.LastTerminalOperation is { } terminal &&
                  terminal.Decision == decision
                    ? terminal
                    : (FileTransferFallbackTailYieldOperation?)null;
        return operation is
        {
            PairedRelease: { IsDefault: false },
        };
    }

    internal static bool
        HasEffectiveFallbackTailPairedReleaseForRouteTransition(
            FileTransferCoordinatorState state)
    {
        var fallbackTail =
            state.PersistentState.FallbackTailYield;
        if (fallbackTail.CurrentOperation is not
            {
                PairedRelease: { IsDefault: false } release,
                PairingState:
                    FileTransferFallbackTailYieldPairingState.ReleaseConsumed,
            } operation ||
            fallbackTail.LastConsumedReservation is not
                { } reservation ||
            reservation.Release != release ||
            fallbackTail.LastConsumingInstallReceipt is not
                { IsDefault: false } consumingInstall ||
            !IsFallbackTailYieldScopeCurrent(
                state,
                GetEffectiveFallbackTailYieldScope(operation),
                out _))
        {
            return false;
        }

        var lifecycle = GetRuntimeUnlockRetryLifecycle(state);
        RuntimeUnlockRetryLifecycleAttemptSnapshot? effective =
            lifecycle.InstalledAttempt is
            {
                State:
                    RuntimeUnlockRetryLifecycleAttemptState.Installed or
                    RuntimeUnlockRetryLifecycleAttemptState.AliasBound or
                    RuntimeUnlockRetryLifecycleAttemptState.SendInFlight,
            } installed
                ? installed
                : lifecycle.PendingAttempt is null &&
                  lifecycle.InstalledAttempt is null &&
                  lifecycle.RetainedPredecessor is null &&
                  lifecycle.BoundedClosure is null &&
                  lifecycle.LastTerminalAttempt is
                  {
                      State:
                          RuntimeUnlockRetryLifecycleAttemptState.Settled,
                      Settlement.Outcome:
                          RuntimeUnlockRetryDispatchSettlementOutcome
                              .DispatchObserved,
                  } terminal
                    ? terminal
                    : fallbackTail.LastConsumingInstallReceipt is
                          { IsDefault: false } consumedInstall
                        ? TryGetExactlyInvalidatedRuntimeUnlockRetryAttempt(
                            lifecycle,
                            consumedInstall)
                        : null;
        return effective is
               {
                   InstallReceipt: { IsDefault: false },
               } attempt &&
               IsRuntimeUnlockRetryInstallDescendedFromFallbackRelease(
                   lifecycle,
                   attempt,
                   reservation,
                   consumingInstall);
    }

    private static bool
        TryCreateFallbackTailPairedReleaseCarryForwardScope(
            FileTransferCoordinatorState state,
            FileTransferFallbackTailYieldScope sourceScope,
            long targetTransportEpoch,
            out FileTransferFallbackTailYieldScope targetScope,
            out string rejectionReason)
    {
        targetScope = default;
        rejectionReason = "accepted";
        if (state.IsTerminal)
        {
            rejectionReason = "transfer_terminal";
            return false;
        }

        if (state.CurrentRuntimeOwner is not { } owner ||
            state.CurrentRoutePublicationToken is not { } publication ||
            state.CurrentLeg is not { } leg)
        {
            rejectionReason =
                "fallback_tail_paired_release_carry_forward_scope_missing";
            return false;
        }

        var checkpointSnapshot =
            NormalizeFallbackTailYieldCheckpointRequestId(
                state.CurrentFallbackCheckpointLease);
        var expectedSourceOwnerKey = BuildFallbackTailYieldOwnerKey(
            sourceScope.SessionId,
            sourceScope.TransferId,
            sourceScope.RouteToken,
            sourceScope.ProtocolVersion,
            sourceScope.LiveRouteEpoch,
            sourceScope.LegGeneration,
            sourceScope.BridgeRecoveryGeneration,
            sourceScope.TransportEpoch,
            sourceScope.CheckpointRequestIdSnapshot);
        var expectedSourcePairKey = BuildFallbackTailYieldPairKey(
            sourceScope.SessionId,
            sourceScope.TransferId,
            sourceScope.RouteToken,
            sourceScope.ProtocolVersion,
            sourceScope.LiveRouteEpoch,
            sourceScope.TransportEpoch,
            sourceScope.CheckpointRequestIdSnapshot);
        if (targetTransportEpoch <= sourceScope.TransportEpoch ||
            leg.TransportEpochId != targetTransportEpoch ||
            !string.Equals(
                sourceScope.SessionId,
                state.SessionId,
                StringComparison.Ordinal) ||
            !string.Equals(
                sourceScope.TransferId,
                state.TransferId,
                StringComparison.Ordinal) ||
            sourceScope.Direction != state.Direction ||
            sourceScope.RuntimeOwner != owner ||
            sourceScope.RoutePublication != publication ||
            sourceScope.Route != state.RouteSelection.Route ||
            !string.Equals(
                sourceScope.RouteToken,
                state.RouteSelection.TelemetryToken,
                StringComparison.Ordinal) ||
            sourceScope.ProtocolVersion !=
                state.RouteSelection.ProtocolVersion ||
            sourceScope.LiveRouteEpoch !=
                (state.CurrentLiveRouteEpoch?.EpochId ??
                 leg.LiveRouteEpochId) ||
            sourceScope.TerminalGeneration != state.TerminalGeneration ||
            sourceScope.LegGeneration != leg.Generation ||
            leg.BridgeRecoveryGeneration <
                sourceScope.BridgeRecoveryGeneration ||
            !string.Equals(
                sourceScope.CheckpointRequestIdSnapshot,
                checkpointSnapshot,
                StringComparison.Ordinal) ||
            sourceScope.CheckpointGeneration != leg.CheckpointGeneration ||
            !string.Equals(
                sourceScope.DataSessionIdentity,
                state.CurrentDataSessionIdentity,
                StringComparison.Ordinal) ||
            sourceScope.DataSessionGeneration !=
                state.CurrentDataSessionGeneration ||
            !string.Equals(
                sourceScope.OwnerKey,
                expectedSourceOwnerKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                sourceScope.PairKey,
                expectedSourcePairKey,
                StringComparison.Ordinal))
        {
            rejectionReason =
                "fallback_tail_paired_release_carry_forward_scope_mismatch";
            return false;
        }

        var targetOwnerKey = BuildFallbackTailYieldOwnerKey(
            state.SessionId,
            state.TransferId,
            state.RouteSelection.TelemetryToken,
            state.RouteSelection.ProtocolVersion,
            state.CurrentLiveRouteEpoch?.EpochId ?? leg.LiveRouteEpochId,
            leg.Generation,
            leg.BridgeRecoveryGeneration,
            targetTransportEpoch,
            checkpointSnapshot);
        var targetPairKey = BuildFallbackTailYieldPairKey(
            state.SessionId,
            state.TransferId,
            state.RouteSelection.TelemetryToken,
            state.RouteSelection.ProtocolVersion,
            state.CurrentLiveRouteEpoch?.EpochId ?? leg.LiveRouteEpochId,
            targetTransportEpoch,
            checkpointSnapshot);
        targetScope = new FileTransferFallbackTailYieldScope(
            state.SessionId,
            state.TransferId,
            state.Direction,
            owner,
            publication,
            state.RouteSelection.Route,
            state.RouteSelection.TelemetryToken,
            state.RouteSelection.ProtocolVersion,
            state.CurrentLiveRouteEpoch?.EpochId ?? leg.LiveRouteEpochId,
            state.TerminalGeneration,
            leg.Generation,
            targetTransportEpoch,
            leg.BridgeRecoveryGeneration,
            checkpointSnapshot,
            leg.CheckpointGeneration,
            state.CurrentDataSessionIdentity,
            state.CurrentDataSessionGeneration,
            targetOwnerKey,
            targetPairKey);
        if (!targetScope.HasCompleteExecutionScope)
        {
            targetScope = default;
            rejectionReason =
                "fallback_tail_paired_release_carry_forward_scope_incomplete";
            return false;
        }

        return true;
    }

    private static FileTransferFallbackTailYieldScope
        CreateFallbackTailYieldScope(
            FileTransferCoordinatorState state,
            FallbackTailYieldDecision decision)
    {
        if (state.CurrentRuntimeOwner is not { } owner ||
            state.CurrentLeg is not { } leg)
        {
            return default;
        }

        return new FileTransferFallbackTailYieldScope(
            state.SessionId,
            state.TransferId,
            state.Direction,
            owner,
            state.CurrentRoutePublicationToken,
            state.RouteSelection.Route,
            state.RouteSelection.TelemetryToken,
            state.RouteSelection.ProtocolVersion,
            state.CurrentLiveRouteEpoch?.EpochId ?? leg.LiveRouteEpochId,
            state.TerminalGeneration,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            decision.CheckpointRequestIdSnapshot,
            leg.CheckpointGeneration,
            state.CurrentDataSessionIdentity,
            state.CurrentDataSessionGeneration,
            decision.OwnerKey,
            decision.PairKey);
    }

    private static bool TryCreateCurrentFallbackTailYieldScope(
        FileTransferCoordinatorState state,
        out FileTransferFallbackTailYieldScope scope)
    {
        scope = default;
        if (state.IsTerminal ||
            state.CurrentRuntimeOwner is not { } owner ||
            state.CurrentRoutePublicationToken is not { } publication ||
            state.CurrentLeg is not { } leg)
        {
            return false;
        }

        var checkpointSnapshot =
            NormalizeFallbackTailYieldCheckpointRequestId(
                state.CurrentFallbackCheckpointLease);
        var liveRouteEpoch =
            state.CurrentLiveRouteEpoch?.EpochId ??
            leg.LiveRouteEpochId;
        var ownerKey = BuildFallbackTailYieldOwnerKey(
            state.SessionId,
            state.TransferId,
            state.RouteSelection.TelemetryToken,
            state.RouteSelection.ProtocolVersion,
            liveRouteEpoch,
            leg.Generation,
            leg.BridgeRecoveryGeneration,
            leg.TransportEpochId,
            checkpointSnapshot);
        var pairKey = BuildFallbackTailYieldPairKey(
            state.SessionId,
            state.TransferId,
            state.RouteSelection.TelemetryToken,
            state.RouteSelection.ProtocolVersion,
            liveRouteEpoch,
            leg.TransportEpochId,
            checkpointSnapshot);
        scope = new FileTransferFallbackTailYieldScope(
            state.SessionId,
            state.TransferId,
            state.Direction,
            owner,
            publication,
            state.RouteSelection.Route,
            state.RouteSelection.TelemetryToken,
            state.RouteSelection.ProtocolVersion,
            liveRouteEpoch,
            state.TerminalGeneration,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            checkpointSnapshot,
            leg.CheckpointGeneration,
            state.CurrentDataSessionIdentity,
            state.CurrentDataSessionGeneration,
            ownerKey,
            pairKey);
        return scope.HasCompleteExecutionScope;
    }

    private static bool IsFallbackTailYieldScopeCurrent(
        FileTransferCoordinatorState state,
        FileTransferFallbackTailYieldScope scope,
        out string rejectionReason)
    {
        rejectionReason = "current";
        if (state.IsTerminal)
        {
            rejectionReason = "transfer_terminal";
            return false;
        }

        if (state.CurrentRuntimeOwner is not { } owner ||
            state.CurrentLeg is not { } leg ||
            state.CurrentRoutePublicationToken is not { } publication)
        {
            rejectionReason = "fallback_tail_yield_scope_missing";
            return false;
        }

        if (!string.Equals(
                scope.SessionId,
                state.SessionId,
                StringComparison.Ordinal) ||
            !string.Equals(
                scope.TransferId,
                state.TransferId,
                StringComparison.Ordinal) ||
            scope.Direction != state.Direction ||
            scope.RuntimeOwner != owner ||
            scope.RoutePublication != publication ||
            scope.Route != state.RouteSelection.Route ||
            !string.Equals(
                scope.RouteToken,
                state.RouteSelection.TelemetryToken,
                StringComparison.Ordinal) ||
            scope.ProtocolVersion != state.RouteSelection.ProtocolVersion ||
            scope.LiveRouteEpoch !=
                (state.CurrentLiveRouteEpoch?.EpochId ??
                 leg.LiveRouteEpochId) ||
            scope.TerminalGeneration != state.TerminalGeneration ||
            scope.LegGeneration != leg.Generation ||
            scope.TransportEpoch != leg.TransportEpochId ||
            scope.BridgeRecoveryGeneration !=
                leg.BridgeRecoveryGeneration ||
            !string.Equals(
                scope.CheckpointRequestIdSnapshot,
                NormalizeFallbackTailYieldCheckpointRequestId(
                    state.CurrentFallbackCheckpointLease),
                StringComparison.Ordinal) ||
            scope.CheckpointGeneration != leg.CheckpointGeneration ||
            !string.Equals(
                scope.DataSessionIdentity,
                state.CurrentDataSessionIdentity,
                StringComparison.Ordinal) ||
            scope.DataSessionGeneration !=
                state.CurrentDataSessionGeneration)
        {
            rejectionReason = "fallback_tail_yield_scope_mismatch";
            return false;
        }

        return true;
    }

    internal static bool TryGetCurrentFallbackTailYieldOperationScope(
        FileTransferCoordinatorState state,
        FallbackTailYieldDecision decision,
        out FileTransferFallbackTailYieldOperation operation,
        out FileTransferFallbackTailYieldScope scope,
        out string rejectionReason)
    {
        operation = default;
        scope = default;
        rejectionReason = "current";
        if (decision.IsDefault)
        {
            rejectionReason = "fallback_tail_yield_authority_missing";
            return false;
        }

        if (state.PersistentState.FallbackTailYield.CurrentOperation is not
                { } currentOperation ||
            currentOperation.Decision != decision)
        {
            rejectionReason = "fallback_tail_yield_authority_stale";
            return false;
        }

        if (!decision.Accepted ||
            decision.AuthorityId != IssueAuthorityId(
                "fallback_tail_yield",
                state,
                decision.YieldGeneration))
        {
            rejectionReason = decision.Accepted
                ? "fallback_tail_yield_authority_id_mismatch"
                : "fallback_tail_yield_authority_tuple_mismatch";
            return false;
        }

        if (currentOperation.PairedReleaseCarryForward is { } carryForward &&
            (carryForward.IsDefault ||
             carryForward.Decision != decision ||
             currentOperation.PairedRelease is not { } release ||
             carryForward.PairedRelease != release ||
             carryForward.SourceScope != currentOperation.Scope ||
             currentOperation.PairingState is not
                 (FileTransferFallbackTailYieldPairingState.Paired or
                  FileTransferFallbackTailYieldPairingState.ReleaseReserved or
                  FileTransferFallbackTailYieldPairingState.ReleaseConsumed)))
        {
            rejectionReason =
                "fallback_tail_paired_release_carry_forward_stale";
            return false;
        }

        var effectiveScope =
            GetEffectiveFallbackTailYieldScope(currentOperation);
        if (!IsFallbackTailYieldScopeCurrent(
                state,
                effectiveScope,
                out rejectionReason))
        {
            if (currentOperation.PairedReleaseCarryForward is not null)
            {
                rejectionReason =
                    "fallback_tail_paired_release_carry_forward_stale";
            }
            else if (string.Equals(
                         rejectionReason,
                         "fallback_tail_yield_scope_mismatch",
                         StringComparison.Ordinal))
            {
                rejectionReason =
                    "fallback_tail_yield_authority_tuple_mismatch";
            }

            return false;
        }

        operation = currentOperation;
        scope = effectiveScope;
        return true;
    }

    public static bool IsFallbackTailYieldDecisionCurrent(
        FileTransferCoordinatorState state,
        FallbackTailYieldDecision decision,
        out string rejectionReason)
        => TryGetCurrentFallbackTailYieldOperationScope(
            state,
            decision,
            out _,
            out _,
            out rejectionReason);

    public static bool IsFallbackTailYieldTransportEpochReauthorizationCurrent(
        FileTransferCoordinatorState state,
        FallbackTailYieldTransportEpochReauthorization reauthorization,
        out string rejectionReason)
        => IsFallbackTailYieldTransportEpochReauthorizationCurrent(
            state,
            reauthorization,
            allowTransportEpochDecisionReplacement: false,
            out rejectionReason);

    private static bool
        IsFallbackTailYieldTransportEpochReauthorizationCurrent(
            FileTransferCoordinatorState state,
            FallbackTailYieldTransportEpochReauthorization reauthorization,
            bool allowTransportEpochDecisionReplacement,
            out string rejectionReason)
    {
        rejectionReason = "current";
        if (reauthorization.IsDefault)
        {
            rejectionReason = "fallback_tail_yield_transport_epoch_reauthorization_missing";
            return false;
        }

        if (GetPendingFallbackTailYieldTransportEpochReauthorization(state) is not { } pending ||
            pending != reauthorization)
        {
            rejectionReason = "fallback_tail_yield_transport_epoch_reauthorization_stale";
            return false;
        }

        if (GetCurrentFallbackTailYieldDecision(state) is not null)
        {
            rejectionReason = "fallback_tail_yield_transport_epoch_reauthorization_lineage_conflict";
            return false;
        }

        return IsFallbackTailYieldTransportEpochReauthorizationTargetScopeCurrent(
            state,
            reauthorization,
            allowTransportEpochDecisionReplacement,
            out rejectionReason);
    }

    internal static bool IsFallbackTailYieldTransportEpochReauthorizationTargetScopeCurrent(
        FileTransferCoordinatorState state,
        FallbackTailYieldTransportEpochReauthorization reauthorization,
        out string rejectionReason)
        => IsFallbackTailYieldTransportEpochReauthorizationTargetScopeCurrent(
            state,
            reauthorization,
            allowTransportEpochDecisionReplacement: false,
            out rejectionReason);

    internal static bool
        IsFallbackTailYieldTransportEpochReauthorizationTargetScopeCurrent(
            FileTransferCoordinatorState state,
            FallbackTailYieldTransportEpochReauthorization reauthorization,
            bool allowTransportEpochDecisionReplacement,
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

        if (!allowTransportEpochDecisionReplacement &&
            state.CurrentTransportEpochDecision is { } currentTransport &&
            currentTransport !=
                reauthorization.TargetTransportEpochDecision)
        {
            rejectionReason =
                "fallback_tail_yield_transport_epoch_reauthorization_transport_decision_stale";
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
            !reauthorization.TargetTransportEpochDecision.Accepted ||
            reauthorization.TargetTransportEpochDecision.ActionKind !=
                TransportEpochActionKind.Adopt ||
            reauthorization.TargetTransportEpochDecision
                    .TargetTransportEpoch !=
                reauthorization.TargetTransportEpoch ||
            reauthorization.TargetTransportEpochDecision.TransportEpoch >=
                reauthorization.TargetTransportEpoch ||
            reauthorization.TargetTransportEpochDecision.TargetRoute !=
                FileTransferRoute.PostTunaFallbackV6 ||
            reauthorization.TargetTransportEpochDecision
                    .TargetProtocolVersion !=
                FileTransferProtocol.ProtocolVersionV6 ||
            reauthorization.TargetTransportEpochDecision.TargetTransport !=
                FileTransferTransportKind.RegularNkn ||
            !string.Equals(
                reauthorization.TargetDataSessionIdentity,
                state.CurrentDataSessionIdentity,
                StringComparison.Ordinal) ||
            reauthorization.TargetDataSessionGeneration !=
                state.CurrentDataSessionGeneration ||
            !string.Equals(
                reauthorization.TargetCheckpointRequestIdSnapshot,
                checkpointSnapshot,
                StringComparison.Ordinal) ||
            reauthorization.TargetCheckpointGeneration !=
                leg.CheckpointGeneration ||
            reauthorization.TargetCheckpointLease !=
                state.CurrentFallbackCheckpointLease ||
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
        if (GetPendingFallbackTailYieldTransportEpochReauthorization(state) is not { } pending ||
            pending != reauthorization)
        {
            return MutationRejected(
                state,
                reason,
                "fallback_tail_yield_transport_epoch_reauthorization_stale");
        }

        var targetTransportDecision =
            state.CurrentTransportEpochDecision;
        if (state.IsTerminal ||
            GetCurrentFallbackTailYieldDecision(state) is not null ||
            state.CurrentRuntimeOwner is not { } owner ||
            state.CurrentLeg is not { } leg ||
            state.CurrentRoutePublicationToken is not { } publication ||
            publication != reauthorization.TargetRoutePublicationToken ||
            !IsRoutePublicationTokenCurrent(state, publication, out _) ||
            state.CurrentBridgeRecoveryDecision != reauthorization.TargetBridgeRecoveryDecision ||
            targetTransportDecision is not { } exactTransportDecision ||
            exactTransportDecision.IsDefault ||
            !exactTransportDecision.Accepted ||
            exactTransportDecision.ActionKind !=
                TransportEpochActionKind.Adopt ||
            exactTransportDecision.RuntimeOwner != owner ||
            exactTransportDecision.CurrentRoute !=
                state.RouteSelection.Route ||
            exactTransportDecision.CurrentProtocolVersion !=
                state.RouteSelection.ProtocolVersion ||
            exactTransportDecision.CurrentLiveRouteEpoch !=
                (state.CurrentLiveRouteEpoch?.EpochId ??
                 leg.LiveRouteEpochId) ||
            exactTransportDecision.CurrentRuntimeGeneration !=
                owner.RuntimeGeneration ||
            exactTransportDecision.CurrentTerminalGeneration !=
                state.TerminalGeneration ||
            exactTransportDecision.CurrentLegGeneration !=
                leg.Generation ||
            exactTransportDecision.TransportEpoch !=
                reauthorization.TargetTransportEpoch ||
            exactTransportDecision.TargetTransportEpoch !=
                targetTransportEpoch ||
            exactTransportDecision.TargetTransportEpoch <=
                exactTransportDecision.TransportEpoch ||
            exactTransportDecision.BridgeRecoveryGeneration >
                leg.BridgeRecoveryGeneration ||
            exactTransportDecision.TargetRoute !=
                FileTransferRoute.PostTunaFallbackV6 ||
            exactTransportDecision.TargetProtocolVersion !=
                FileTransferProtocol.ProtocolVersionV6 ||
            exactTransportDecision.TargetTransport !=
                FileTransferTransportKind.RegularNkn ||
            state.RouteSelection.Route != FileTransferRoute.PostTunaFallbackV6 ||
            state.RouteSelection.ProtocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            owner != reauthorization.TargetRuntimeOwner ||
            state.TerminalGeneration != reauthorization.TargetTerminalGeneration ||
            leg.Generation != reauthorization.TargetLegGeneration ||
            targetTransportEpoch != leg.TransportEpochId ||
            targetTransportEpoch < reauthorization.TargetTransportEpoch ||
            !string.Equals(
                state.CurrentDataSessionIdentity,
                reauthorization.TargetDataSessionIdentity,
                StringComparison.Ordinal) ||
            state.CurrentDataSessionGeneration !=
                reauthorization.TargetDataSessionGeneration ||
            !string.Equals(
                NormalizeFallbackTailYieldCheckpointRequestId(state.CurrentFallbackCheckpointLease),
                reauthorization.TargetCheckpointRequestIdSnapshot,
                StringComparison.Ordinal) ||
            leg.CheckpointGeneration !=
                reauthorization.TargetCheckpointGeneration ||
            state.CurrentFallbackCheckpointLease !=
                reauthorization.TargetCheckpointLease)
        {
            return MutationRejected(
                state,
                reason,
                "fallback_tail_yield_transport_epoch_reauthorization_rebind_mismatch");
        }

        if (targetTransportEpoch == reauthorization.TargetTransportEpoch &&
            leg.BridgeRecoveryGeneration ==
                reauthorization.TargetBridgeRecoveryGeneration &&
            exactTransportDecision ==
                reauthorization.TargetTransportEpochDecision)
        {
            return Decision(state, reason);
        }

        var nextGeneration = Math.Max(
            reauthorization.ReauthorizationGeneration + 1,
            GetLastFallbackTailYieldTransportEpochReauthorizationGeneration(
                state) + 1);
        var rebound = reauthorization with
        {
            TargetTransportEpoch = targetTransportEpoch,
            TargetTransportEpochDecision = exactTransportDecision,
            TargetBridgeRecoveryGeneration = leg.BridgeRecoveryGeneration,
            ReauthorizationGeneration = nextGeneration,
            Reason = NormalizeReason(reason),
        };
        var fallbackTail = state.PersistentState.FallbackTailYield;
        var reboundState = ApplyFallbackTailYieldSubstate(
            state,
            fallbackTail with
            {
                PendingReauthorization = rebound,
                PendingReauthorizationPeerProof = null,
                RetainedPredecessor = rebound.PredecessorDecision,
                LastReauthorizationGeneration = nextGeneration,
            });
        return Decision(
            reboundState,
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
        var retryLifecycle = GetRuntimeUnlockRetryLifecycle(state);
        var nextRetryLifecycle = settlingRetained
            ? retryLifecycle
            : retryLifecycle with
            {
                RepairSettlementRelease =
                    IsEligibleRuntimeUnlockRetryRepairSettlementRelease(
                        state,
                        settlement)
                        ? new RuntimeUnlockRetryLifecycleRepairSettlementRelease(
                            settlement,
                            receipt,
                            RuntimeUnlockRetryLifecycleRepairSettlementReleaseState
                                .Available)
                        : null,
            };
        var nextState = state with
        {
            PersistentState = state.PersistentState with
            {
                RegularV4FrontierRepairPressure = nextPressureState,
                RuntimeUnlockRetryLifecycle = nextRetryLifecycle,
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
            FileTransferReceiveRecoveryObservation receiveRecoveryObservation)
    {
        if (!TryCreateRegularV4FrontierRepairPressureRecoveryHandoffTransition(
                state,
                receiveRecoveryObservation,
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

        if (transition.ReceiveRecoveryOperation.IsDefault)
        {
            return RegularV4PressureRecoveryHandoffDecision(
                state,
                RegularV4FrontierRepairPressureRecoveryHandoffStatus.Rejected,
                "filetransfer_receive_recovery_operation_missing");
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
        var operationCommit = IssueReceiveRecoveryOperation(
            settledState,
            transition.ReceiveRecoveryOperation.Observation,
            BridgeRecoveryKind.BridgeReceiveStallRecovery,
            transition.ReceiveRecoveryOperation.ExecutionCommand
                .CheckpointRequestId);
        if (operationCommit.Status !=
                FileTransferReceiveRecoveryTransitionStatus.Accepted ||
            operationCommit.Authority != transition.ReceiveRecoveryOperation)
        {
            return RegularV4PressureRecoveryHandoffDecision(
                state,
                RegularV4FrontierRepairPressureRecoveryHandoffStatus.Rejected,
                operationCommit.Status ==
                    FileTransferReceiveRecoveryTransitionStatus.Accepted
                    ? "regular_v4_frontier_repair_pressure_recovery_handoff_operation_mismatch"
                    : operationCommit.Reason);
        }

        var committedState = operationCommit.State;
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
            transition,
            operationCommit.InitialActivationHoldDecision);
    }

    private static bool TryCreateRegularV4FrontierRepairPressureRecoveryHandoffTransition(
        FileTransferCoordinatorState state,
        FileTransferReceiveRecoveryObservation receiveRecoveryObservation,
        out RegularV4FrontierRepairPressureRecoveryHandoffTransition transition,
        out string rejectionReason)
    {
        transition = default;
        rejectionReason = "accepted";
        var pressureState = state.PersistentState.RegularV4FrontierRepairPressure;
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
        FileTransferReceiveRecoveryOperationAuthority
            receiveRecoveryOperation;
        BridgeRecoveryDecision? bridgeRecoveryDecisionCandidate;
        RecoveryLivenessLease? recoveryLivenessLeaseCandidate;
        FileTransferBridgeRecoveryExecutionCommand? executionCommandCandidate;
        var operationDecision = IssueReceiveRecoveryOperation(
            projectedState,
            receiveRecoveryObservation,
            BridgeRecoveryKind.BridgeReceiveStallRecovery,
            receiveRecoveryObservation.CheckpointRequestId);
        if (operationDecision.Status !=
                FileTransferReceiveRecoveryTransitionStatus.Accepted ||
            operationDecision.Authority is not
                { IsDefault: false } operationAuthority)
        {
            rejectionReason = operationDecision.Reason;
            return false;
        }

        receiveRecoveryOperation = operationAuthority;
        bridgeRecoveryDecisionCandidate =
            operationAuthority.BridgeRecoveryDecision;
        recoveryLivenessLeaseCandidate =
            operationAuthority.RecoveryLivenessLease;
        executionCommandCandidate =
            operationAuthority.ExecutionCommand;

        if (bridgeRecoveryDecisionCandidate is not { } bridgeRecoveryDecision ||
            bridgeRecoveryDecision.IsDefault ||
            !bridgeRecoveryDecision.Accepted ||
            bridgeRecoveryDecision.RecoveryKind != BridgeRecoveryKind.BridgeReceiveStallRecovery ||
            recoveryLivenessLeaseCandidate is not { } recoveryLivenessLease ||
            recoveryLivenessLease.IsDefault ||
            recoveryLivenessLease.State != RecoveryLivenessLeaseState.Active ||
            executionCommandCandidate is not { } executionCommand ||
            executionCommand.IsDefault)
        {
            rejectionReason = "regular_v4_recovery_authority_incomplete";
            return false;
        }

        transition = new RegularV4FrontierRepairPressureRecoveryHandoffTransition(
            binding,
            settlement,
            settlementCommand,
            receiveRecoveryOperation);
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

        if (transition.ReceiveRecoveryOperation.IsDefault ||
            transition.BridgeRecoveryDecision.RecoveryKind !=
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

        var operationAuthority = transition.ReceiveRecoveryOperation;
        var observation = operationAuthority.Observation;
        var owner = state.CurrentRuntimeOwner;
        var leg = state.CurrentLeg;
        if (owner is null ||
            leg is null ||
            observation.RuntimeOwner != owner ||
            observation.Route != state.RouteSelection.Route ||
            observation.ProtocolVersion != state.RouteSelection.ProtocolVersion ||
            observation.LiveRouteEpoch != (state.CurrentLiveRouteEpoch?.EpochId ?? 0) ||
            observation.LegGeneration != leg.Generation ||
            observation.TransportEpoch != leg.TransportEpochId ||
            observation.BridgeRecoveryGeneration != leg.BridgeRecoveryGeneration ||
            (observation.CheckpointRequestId is not null &&
             !string.Equals(
                 observation.CheckpointRequestId,
                 leg.CheckpointRequestId,
                 StringComparison.Ordinal)))
        {
            rejectionReason = "regular_v4_frontier_repair_pressure_recovery_handoff_recovery_scope_mismatch";
            return false;
        }

        var expectedBridgeDecision = CreateBridgeRecoveryDecision(
            state,
            BridgeRecoveryKind.BridgeReceiveStallRecovery,
            observation.Reason,
            Math.Max(1, state.LastBridgeRecoveryDecisionGeneration + 1));
        var expectedLease = IssueRecoveryLivenessLease(
            state,
            Math.Max(1, state.LastRecoveryLivenessLeaseGeneration + 1),
            RecoveryLivenessLeaseState.Active,
            observation.Reason);
        var expectedExecutionCommand = expectedBridgeDecision is { } bridgeDecision &&
                                       expectedLease is { } lease
            ? new FileTransferBridgeRecoveryExecutionCommand(
                bridgeDecision,
                lease,
                observation.CheckpointRequestId)
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

        {
            var expectedOperationGeneration = Math.Max(
                1,
                state.PersistentState.FileTransferReceiveRecovery
                    .LastOperationGeneration + 1);
            if (!TryValidateReceiveRecoveryObservationCurrent(
                    state,
                    observation,
                    observation.CheckpointRequestId,
                    out rejectionReason) ||
                operationAuthority.BridgeRecoveryDecision !=
                    transition.BridgeRecoveryDecision ||
                operationAuthority.RecoveryLivenessLease !=
                    transition.RecoveryLivenessLease ||
                operationAuthority.ExecutionCommand !=
                    transition.BridgeRecoveryExecutionCommand ||
                operationAuthority.OperationGeneration !=
                    expectedOperationGeneration ||
                operationAuthority.AuthorityId !=
                    IssueReceiveRecoveryOperationAuthorityId(
                        state,
                        expectedOperationGeneration))
            {
                rejectionReason =
                    "regular_v4_frontier_repair_pressure_recovery_handoff_operation_mismatch";
                return false;
            }
        }

        var projectedPressureState = pressureState with
        {
            PendingBinding = null,
            PendingInstallCommand = null,
            InstalledBinding = null,
            InstalledInstallCommand = null,
            InstalledInstallReceipt = null,
            LastSettlement = expectedSettlement,
        };
        var projectedState = state with
        {
            CurrentRepairReplayDecision = null,
            PersistentState = state.PersistentState with
            {
                RegularV4FrontierRepairPressure = projectedPressureState,
            },
        };
        var projectedOperation = IssueReceiveRecoveryOperation(
            projectedState,
            observation,
            BridgeRecoveryKind.BridgeReceiveStallRecovery,
            observation.CheckpointRequestId);
        if (projectedOperation.Status !=
                FileTransferReceiveRecoveryTransitionStatus.Accepted ||
            projectedOperation.Authority != transition.ReceiveRecoveryOperation)
        {
            rejectionReason =
                projectedOperation.Status ==
                    FileTransferReceiveRecoveryTransitionStatus.Accepted
                    ? "regular_v4_frontier_repair_pressure_recovery_handoff_operation_mismatch"
                    : projectedOperation.Reason;
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
            !transition.ReceiveRecoveryOperation.IsDefault &&
            (state.PersistentState.FileTransferReceiveRecovery.CurrentOperation
                 is { } currentOperation &&
             currentOperation.Authority ==
                 transition.ReceiveRecoveryOperation &&
             currentOperation.Authority.BridgeRecoveryDecision ==
                 transition.BridgeRecoveryDecision &&
             currentOperation.Authority.RecoveryLivenessLease ==
                 transition.RecoveryLivenessLease ||
             state.PersistentState.FileTransferReceiveRecovery
                 .LastTerminalOperation is { } terminalOperation &&
            terminalOperation.Authority ==
                transition.ReceiveRecoveryOperation);

    private static RegularV4FrontierRepairPressureRecoveryHandoffDecision
        RegularV4PressureRecoveryHandoffDecision(
            FileTransferCoordinatorState state,
            RegularV4FrontierRepairPressureRecoveryHandoffStatus status,
            string reason,
            RegularV4FrontierRepairPressureRecoveryHandoffTransition? transition = null,
            RuntimeUnlockInitialActivationReceiveRecoveryHoldDecision?
                initialActivationHoldDecision = null)
        => new(status, NormalizeReason(reason), transition)
        {
            State = state,
            InitialActivationHoldDecision = initialActivationHoldDecision,
        };

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
        RuntimeUnlockRetryReleaseKind releaseKind,
        FileTransferAuthorityId? releaseAuthorityId,
        int releaseGeneration,
        string releaseOutcome)
        => AuthorizeRuntimeUnlockRetryDispatchCore(
            state,
            request,
            releaseKind,
            releaseAuthorityId,
            releaseGeneration,
            releaseOutcome,
            allowCoordinatorPreparedSuccessor: false);

    private static RuntimeUnlockRetryDispatchDecision
        AuthorizeRuntimeUnlockRetryDispatchCore(
            FileTransferCoordinatorState state,
            RuntimeUnlockRetryDispatchAuthorityRequest request,
            RuntimeUnlockRetryReleaseKind releaseKind,
            FileTransferAuthorityId? releaseAuthorityId,
            int releaseGeneration,
            string releaseOutcome,
            bool allowCoordinatorPreparedSuccessor)
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

        var retryLifecycle = GetRuntimeUnlockRetryLifecycle(state);
        if (retryLifecycle.PendingScopeInvalidationCommand is not null)
        {
            return Decision(
                RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                "retry_dispatch_scope_invalidation_pending");
        }

        RuntimeUnlockRetryLifecycleAttemptSnapshot? currentAttempt =
            retryLifecycle.PendingAttempt ??
            retryLifecycle.InstalledAttempt;

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
        var normalizedReleaseAuthorityId =
            releaseAuthorityId is { IsDefault: false }
                ? releaseAuthorityId
                : null;
        var normalizedReleaseOutcome = NormalizeReason(releaseOutcome);
        if (currentAttempt is null &&
            RuntimeUnlockRetryLifecycleContainsTerminalBoundary(
                retryLifecycle,
                request,
                releaseKind,
                normalizedReleaseAuthorityId,
                releaseGeneration,
                normalizedReleaseOutcome))
        {
            return Decision(
                RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                "retry_dispatch_attempt_already_settled");
        }

        if (currentAttempt is null)
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
        else if (predecessorAuthorityId is not null &&
                 !allowCoordinatorPreparedSuccessor)
        {
            return Decision(
                RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                "retry_dispatch_successor_not_coordinator_prepared");
        }

        if (currentAttempt is { } existingAttempt &&
            existingAttempt.Authority.RecoveryContractGeneration ==
                request.RecoveryContractGeneration &&
            existingAttempt.Authority.RetiredOfferGeneration ==
                request.RetiredOfferGeneration &&
            existingAttempt.Authority.DispatchAttemptGeneration ==
                request.DispatchAttemptGeneration)
        {
            if (!RuntimeUnlockRetryDispatchAuthorityMatchesStableRequest(
                    existingAttempt.Authority,
                    request,
                    out _) ||
                existingAttempt.Request.RegularV4LivenessGeneration !=
                    request.RegularV4LivenessGeneration ||
                existingAttempt.Request.BridgeDecisionGeneration !=
                    request.BridgeDecisionGeneration ||
                existingAttempt.Request.RequestActivePredecessorSupersession !=
                    request.RequestActivePredecessorSupersession)
            {
                return Decision(
                    RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                    "retry_dispatch_attempt_identity_mismatch");
            }

            var existingContract =
                new RuntimeUnlockRetryDispatchContract(
                    existingAttempt.Authority,
                    existingAttempt.Request.RequestActivePredecessorSupersession,
                    existingAttempt.Request.RegularV4LivenessGeneration,
                    existingAttempt.Request.BridgeDecisionGeneration);
            return existingAttempt.IsTerminal
                ? Decision(
                    RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                    "retry_dispatch_attempt_already_settled")
                : Decision(
                    RuntimeUnlockRetryDispatchDecisionStatus.Granted,
                    "idempotent",
                    existingContract);
        }

        var authorizedReleaseKind = releaseKind;
        var authorizedReleaseAuthorityId = releaseAuthorityId;
        var authorizedReleaseGeneration = releaseGeneration;
        var authorizedReleaseOutcome = releaseOutcome;
        FileTransferReceiveRecoveryOperation?
            receiveRecoveryReleaseOperation = null;
        if (currentAttempt is null &&
            TryGetReceiveRecoveryOperationForRetryRequest(
                state,
                request,
                out var matchingRecoveryOperation,
                out var matchingRecoveryLease))
        {
            if (releaseKind ==
                RuntimeUnlockRetryReleaseKind.Unknown)
            {
                authorizedReleaseKind =
                    RuntimeUnlockRetryReleaseKind
                        .ReceiveRecoveryLeaseClosure;
                authorizedReleaseAuthorityId =
                    matchingRecoveryLease.AuthorityId;
                authorizedReleaseGeneration =
                    matchingRecoveryLease.LeaseGeneration;
                authorizedReleaseOutcome =
                    nameof(RecoveryLivenessLeaseState.Retired);
            }

            if (authorizedReleaseKind ==
                    RuntimeUnlockRetryReleaseKind
                        .ReceiveRecoveryLeaseClosure &&
                authorizedReleaseAuthorityId ==
                    matchingRecoveryLease.AuthorityId &&
                authorizedReleaseGeneration ==
                    matchingRecoveryLease.LeaseGeneration &&
                string.Equals(
                    authorizedReleaseOutcome,
                    nameof(RecoveryLivenessLeaseState.Retired),
                    StringComparison.Ordinal))
            {
                receiveRecoveryReleaseOperation =
                    matchingRecoveryOperation;
            }
        }

        if (currentAttempt is { } predecessor)
        {
            if (predecessorAuthorityId != predecessor.Authority.AuthorityId ||
                retryLifecycle.CurrentAuthorizedAuthority is not { } coordinatorPredecessor ||
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

            if (request.RegularV4LivenessGeneration !=
                    predecessor.Request.RegularV4LivenessGeneration ||
                request.BridgeDecisionGeneration !=
                    predecessor.Request.BridgeDecisionGeneration)
            {
                return Decision(
                    RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                    "retry_dispatch_recovery_handoff_tuple_mismatch");
            }

            if (!predecessor.IsTerminal)
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

                if (predecessor.State ==
                    RuntimeUnlockRetryLifecycleAttemptState.SendInFlight)
                {
                    return Decision(
                        RuntimeUnlockRetryDispatchDecisionStatus.Held,
                        "retry_dispatch_predecessor_send_in_flight");
                }

            }
            else
            {
                if (predecessor.Settlement?.Outcome !=
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

                if (predecessor.Settlement is not { } predecessorSettlement ||
                    retryLifecycle.LastSettlement is not { } acceptedPredecessorSettlement ||
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
                        nextState: CloseRuntimeUnlockRetryDispatchChain(
                            state,
                            request,
                            "retry_dispatch_missing_successor_limit_exhausted").State);
                }
            }
        }
        else if (predecessorAuthorityId is not null ||
                 retryLifecycle.CurrentAuthorizedAuthority is not null)
        {
            return Decision(
                RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                "retry_dispatch_predecessor_missing");
        }

        if (authorizedReleaseKind ==
            RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield)
        {
            var originalFallbackTailYield =
                state.PersistentState.FallbackTailYield;
            var currentState =
                InvalidateFallbackTailYieldForDataSessionReplacement(state);
            if (!ReferenceEquals(
                    originalFallbackTailYield,
                    currentState.PersistentState.FallbackTailYield))
            {
                return Decision(
                    RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                    "data_session_replaced",
                    nextState: currentState);
            }
        }

        var releaseValid = currentAttempt is not null
            ? TryValidateInheritedRuntimeUnlockRetryRelease(
                state,
                currentAttempt.Value.Authority,
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
            if (held &&
                releaseRejectionReason ==
                    "receive_recovery_lease_pending" &&
                receiveRecoveryReleaseOperation is { } recoveryOperation)
            {
                if (recoveryOperation.HeldRetryRequest is
                    { } heldRequest &&
                    heldRequest != request)
                {
                    return Decision(
                        RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                        "receive_recovery_lease_retry_request_conflict");
                }

                if (recoveryOperation.HeldRetryRequest is null)
                {
                    var recoveryState = state.PersistentState
                        .FileTransferReceiveRecovery;
                    state = state with
                    {
                        PersistentState = state.PersistentState with
                        {
                            FileTransferReceiveRecovery =
                                recoveryState with
                                {
                                    CurrentOperation =
                                        recoveryOperation with
                                        {
                                            HeldRetryRequest = request,
                                        },
                                },
                        },
                    };
                }

                return new RuntimeUnlockRetryDispatchDecision(
                    RuntimeUnlockRetryDispatchDecisionStatus.Held,
                    releaseRejectionReason,
                    Contract: null)
                {
                    Request = request,
                    State = state,
                };
            }

            return Decision(
                held
                    ? RuntimeUnlockRetryDispatchDecisionStatus.Held
                    : RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                releaseRejectionReason);
        }

        if (retryLifecycle.LastAuthorityGeneration == int.MaxValue)
        {
            return Decision(
                RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                "retry_dispatch_authority_generation_exhausted");
        }

        var authorityGeneration = Math.Max(
            1,
            retryLifecycle.LastAuthorityGeneration + 1);
        var receiveRecoveryOperationGeneration =
            authorizedReleaseKind ==
                RuntimeUnlockRetryReleaseKind
                    .ReceiveRecoveryLeaseClosure
                ? currentAttempt?.Authority
                        .ReceiveRecoveryOperationGeneration ??
                  receiveRecoveryReleaseOperation?.Authority
                      .OperationGeneration ??
                  0
                : 0;
        var receiveRecoveryDataSessionGeneration =
            authorizedReleaseKind ==
                RuntimeUnlockRetryReleaseKind
                    .ReceiveRecoveryLeaseClosure
                ? currentAttempt?.Authority
                        .ReceiveRecoveryDataSessionGeneration ??
                  receiveRecoveryReleaseOperation?.Authority
                      .Observation.DataSessionGeneration ??
                  0
                : 0;
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
            predecessorAuthorityId)
        {
            ListenerRearmRequirement = request.ListenerRearmRequirement,
            ReceiveRecoveryOperationGeneration =
                receiveRecoveryOperationGeneration,
            ReceiveRecoveryDataSessionGeneration =
                receiveRecoveryDataSessionGeneration,
        };
        var contract = new RuntimeUnlockRetryDispatchContract(
            authority,
            request.RequestActivePredecessorSupersession,
            request.RegularV4LivenessGeneration,
            request.BridgeDecisionGeneration);

        var attempt = new RuntimeUnlockRetryLifecycleAttemptSnapshot(
            request,
            authority,
            RuntimeUnlockRetryLifecycleAttemptState.Authorized);
        var nextRetryLifecycle = retryLifecycle with
        {
            PendingAttempt = attempt,
            LastAuthorityGeneration = authorityGeneration,
        };
        var acceptedState =
            ApplyRuntimeUnlockRetryLifecycle(state, nextRetryLifecycle);
        if (authorizedReleaseKind ==
                RuntimeUnlockRetryReleaseKind.ReceiveRecoveryLeaseClosure &&
            acceptedState.PersistentState.FileTransferReceiveRecovery
                .CurrentOperation is not null)
        {
            var reservation = ReserveReceiveRecoveryClosure(
                acceptedState,
                request,
                authority);
            if (!reservation.Accepted)
            {
                return Decision(
                    RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                    reservation.Reason);
            }

            acceptedState = reservation.State;
        }

        if (currentAttempt is null &&
            authorizedReleaseKind ==
                RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield)
        {
            if (acceptedState.PersistentState.FallbackTailYield
                    .CurrentOperation is not
                {
                    PairedRelease: { } pairedRelease,
                })
            {
                return Decision(
                    RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                    "fallback_paired_tail_yield_pending");
            }

            var reservation = ReserveFallbackTailPairedRelease(
                acceptedState,
                pairedRelease,
                authority);
            if (!reservation.Accepted)
            {
                return Decision(
                    RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                    reservation.Reason);
            }

            acceptedState = reservation.State;
        }

        return Decision(
            RuntimeUnlockRetryDispatchDecisionStatus.Granted,
            "accepted",
            contract,
            acceptedState);
    }

    internal static RuntimeUnlockRetryDispatchSettlementDecision
        ObserveRuntimeUnlockRetryDispatchSettlement(
            FileTransferCoordinatorState state,
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

        if (report.IsDefault ||
            !IsCompleteRuntimeUnlockRetryDispatchSettlementAuthority(
                report.Authority))
        {
            return Decision(
                RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected,
                report.IsDefault
                    ? "retry_dispatch_settlement_report_missing"
                    : "retry_dispatch_settlement_authority_incomplete");
        }

        var retryLifecycle = GetRuntimeUnlockRetryLifecycle(state);
        if (retryLifecycle.PendingScopeInvalidationCommand is not null)
        {
            return Decision(
                RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected,
                "retry_dispatch_scope_invalidation_pending");
        }

        if (!string.Equals(
                report.Authority.SessionId,
                state.SessionId,
                StringComparison.Ordinal) ||
            !string.Equals(
                report.Authority.TransferId,
                state.TransferId,
                StringComparison.Ordinal) ||
            report.Authority.Direction != state.Direction ||
            !string.Equals(
                report.Reason,
                NormalizeReason(report.Reason),
                StringComparison.Ordinal))
        {
            return Decision(
                RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected,
                "retry_dispatch_settlement_tuple_invalid");
        }

        var lifecycle = GetRuntimeUnlockRetryLifecycle(state);
        if (lifecycle.PendingAttempt is { } pendingSuccessor &&
            lifecycle.InstalledAttempt is { } installedPredecessor &&
            installedPredecessor.Authority == report.Authority &&
            pendingSuccessor.Authority.PredecessorAuthorityId ==
                installedPredecessor.Authority.AuthorityId)
        {
            return Decision(
                RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected,
                "retry_dispatch_successor_install_pending");
        }

        if ((lifecycle.LastSettlement is { } duplicate &&
             duplicate == report) ||
            RuntimeUnlockRetryLifecycleContainsSettlement(
                lifecycle,
                report))
        {
            return Decision(
                RuntimeUnlockRetryDispatchSettlementDecisionStatus.Idempotent,
                "retry_dispatch_settlement_duplicate");
        }

        var reportAttempt =
            report.Outcome ==
                    RuntimeUnlockRetryDispatchSettlementOutcome.Superseded
                ? lifecycle.RetainedPredecessor
                : lifecycle.InstalledAttempt;
        if (reportAttempt is not { } exactAttempt ||
            exactAttempt.Authority != report.Authority ||
            report.OfferGeneration != exactAttempt.OfferGeneration ||
            !string.Equals(
                report.OfferNonce,
                exactAttempt.OfferNonce,
                StringComparison.Ordinal))
        {
            return Decision(
                RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected,
                "retry_dispatch_settlement_execution_proof_mismatch");
        }

        if (report.DispatchObserved !=
            (report.Outcome ==
             RuntimeUnlockRetryDispatchSettlementOutcome.DispatchObserved))
        {
            return Decision(
                RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected,
                "retry_dispatch_settlement_dispatch_proof_mismatch");
        }

        var currentScopeAttempt =
            report.Outcome ==
                    RuntimeUnlockRetryDispatchSettlementOutcome.Superseded
                ? lifecycle.InstalledAttempt
                : reportAttempt;
        if (currentScopeAttempt is not { } currentScope)
        {
            return Decision(
                RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected,
                "retry_dispatch_settlement_current_attempt_missing");
        }

        if (!IsRuntimeUnlockRetryLifecycleAttemptCurrent(
                state,
                currentScope,
                out var currentnessReason,
                allowTerminalState:
                    report.Outcome ==
                    RuntimeUnlockRetryDispatchSettlementOutcome.TransferTerminal))
        {
            return Decision(
                RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected,
                currentnessReason);
        }

        if (report.Outcome == RuntimeUnlockRetryDispatchSettlementOutcome.Superseded)
        {
            if (lifecycle.RetainedPredecessor is not { } predecessor ||
                lifecycle.InstalledAttempt is not { } successor ||
                predecessor.Authority != report.Authority ||
                successor.Authority.PredecessorAuthorityId !=
                    predecessor.Authority.AuthorityId ||
                report.SuccessorAuthorityId !=
                    successor.Authority.AuthorityId ||
                !IsRuntimeUnlockRetryDispatchSupersededReason(report.Reason))
            {
                return Decision(
                    RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected,
                    "retry_dispatch_settlement_successor_missing");
            }

            var settledPredecessor = predecessor with
            {
                State = RuntimeUnlockRetryLifecycleAttemptState.Settled,
                Settlement = report,
                SuccessorAuthorityId = successor.Authority.AuthorityId,
            };
            var successorLifecycle = lifecycle with
            {
                RetainedPredecessor = null,
                LastTerminalAttempt = settledPredecessor,
                LastSettlement = report,
                AttemptHistory =
                    AppendRuntimeUnlockRetryLifecycleAttemptHistory(
                        lifecycle.AttemptHistory,
                        settledPredecessor),
            };
            return Decision(
                RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted,
                "retry_dispatch_settlement_superseded_predecessor",
                ApplyRuntimeUnlockRetryLifecycle(state, successorLifecycle));
        }

        if (lifecycle.InstalledAttempt is not { } current ||
            !current.IsInstalled ||
            current.Authority != report.Authority)
        {
            return Decision(
                RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected,
                "retry_dispatch_settlement_authority_mismatch");
        }

        var exactBoundOffer =
            report.OfferGeneration > 0 &&
            report.OfferGeneration == current.OfferGeneration &&
            !string.IsNullOrWhiteSpace(report.OfferNonce) &&
            string.Equals(
                report.OfferNonce,
                current.OfferNonce,
                StringComparison.Ordinal);
        var exactPreOfferMissingDispatch =
            report.Outcome ==
                RuntimeUnlockRetryDispatchSettlementOutcome.MissingDispatch &&
            current.State == RuntimeUnlockRetryLifecycleAttemptState.Installed &&
            current.IsInstalled &&
            current.OfferGeneration == 0 &&
            string.IsNullOrWhiteSpace(current.OfferNonce) &&
            report.OfferGeneration == 0 &&
            string.IsNullOrWhiteSpace(report.OfferNonce) &&
            !report.DispatchObserved;
        if (report.Outcome ==
                RuntimeUnlockRetryDispatchSettlementOutcome.DispatchObserved &&
            (!exactBoundOffer ||
             current.State !=
                 RuntimeUnlockRetryLifecycleAttemptState.SendInFlight))
        {
            return Decision(
                RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected,
                "retry_dispatch_settlement_execution_proof_mismatch");
        }

        if (report.Outcome ==
                RuntimeUnlockRetryDispatchSettlementOutcome.MissingDispatch &&
            !((exactBoundOffer &&
               current.State is
                   RuntimeUnlockRetryLifecycleAttemptState.AliasBound or
                   RuntimeUnlockRetryLifecycleAttemptState.SendInFlight) ||
              exactPreOfferMissingDispatch))
        {
            return Decision(
                RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected,
                "retry_dispatch_settlement_execution_proof_mismatch");
        }

        var settled = current with
        {
            State = RuntimeUnlockRetryLifecycleAttemptState.Settled,
            Settlement = report,
            DispatchObserved = report.DispatchObserved,
        };
        var retainForSuccessor =
            report.Outcome ==
            RuntimeUnlockRetryDispatchSettlementOutcome.MissingDispatch;
        var nextLifecycle = lifecycle with
        {
            PendingAttempt = null,
            InstalledAttempt = retainForSuccessor ? settled : null,
            LastTerminalAttempt = settled,
            LastSettlement = report,
            AttemptHistory =
                AppendRuntimeUnlockRetryLifecycleAttemptHistory(
                    lifecycle.AttemptHistory,
                    settled),
        };
        return Decision(
            RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted,
            "retry_dispatch_settlement_accepted",
            ApplyRuntimeUnlockRetryLifecycle(state, nextLifecycle));
    }

    internal static FileTransferReceiveRecoveryDecision IssueReceiveRecoveryOperation(
        FileTransferCoordinatorState state,
        FileTransferReceiveRecoveryObservation observation,
        BridgeRecoveryKind recoveryKind,
        string? checkpointRequestId = null)
    {
        FileTransferReceiveRecoveryDecision Decision(
            FileTransferReceiveRecoveryTransitionStatus status,
            string reason,
            FileTransferReceiveRecoveryOperationAuthority? authority = null,
            FileTransferCoordinatorState? nextState = null)
            => new(status, reason, authority)
            {
                State = nextState ?? state,
            };

        var recoveryState = state.PersistentState.FileTransferReceiveRecovery;
        FileTransferReceiveRecoveryOperation? completedPredecessor =
            null;
        if (recoveryState.CurrentOperation is { } current)
        {
            if (current.Authority.Observation == observation &&
                current.Authority.BridgeRecoveryDecision.RecoveryKind ==
                    recoveryKind &&
                string.Equals(
                    current.Authority.ExecutionCommand.CheckpointRequestId,
                    checkpointRequestId,
                    StringComparison.Ordinal))
            {
                return Decision(
                    FileTransferReceiveRecoveryTransitionStatus.Idempotent,
                    "filetransfer_receive_recovery_operation_duplicate",
                    current.Authority);
            }

            if (!CanSupersedeCompletedReceiveRecoveryOperation(
                    state,
                    current,
                    observation,
                    recoveryKind,
                    checkpointRequestId))
            {
                return Decision(
                    FileTransferReceiveRecoveryTransitionStatus.Rejected,
                    "filetransfer_receive_recovery_operation_conflict");
            }

            completedPredecessor = current;
        }

        if (recoveryState.LastTerminalOperation is { } terminal)
        {
            if (terminal.Authority.Observation == observation &&
                terminal.Authority.BridgeRecoveryDecision.RecoveryKind ==
                    recoveryKind &&
                string.Equals(
                    terminal.Authority.ExecutionCommand.CheckpointRequestId,
                    checkpointRequestId,
                    StringComparison.Ordinal))
            {
                return Decision(
                    FileTransferReceiveRecoveryTransitionStatus.Idempotent,
                    "filetransfer_receive_recovery_operation_terminal_duplicate",
                    terminal.Authority);
            }

            if (string.Equals(
                    terminal.Authority.Observation.DataSessionIdentity,
                    observation.DataSessionIdentity,
                    StringComparison.Ordinal) &&
                terminal.Authority.Observation.DataSessionGeneration ==
                    observation.DataSessionGeneration &&
                terminal.Authority.Observation.ObservationGeneration >=
                    observation.ObservationGeneration)
            {
                return Decision(
                    FileTransferReceiveRecoveryTransitionStatus.Rejected,
                    "filetransfer_receive_recovery_observation_already_terminal");
            }
        }

        if (!TryValidateReceiveRecoveryObservationCurrent(
                state,
                observation,
                checkpointRequestId,
                out var rejectionReason))
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Rejected,
                rejectionReason);
        }

        if (recoveryKind == BridgeRecoveryKind.Unknown)
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Rejected,
                "filetransfer_receive_recovery_kind_missing");
        }

        var pressureState =
            state.PersistentState.RegularV4FrontierRepairPressure;
        if (pressureState.PendingBinding is not null ||
            pressureState.InstalledBinding is not null ||
            pressureState.RetainedPredecessorBinding is not null)
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Rejected,
                "regular_v4_frontier_repair_pressure_retirement_required");
        }

        if (recoveryState.LastOperationGeneration == long.MaxValue)
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Rejected,
                "filetransfer_receive_recovery_operation_generation_exhausted");
        }

        if (state.LastBridgeRecoveryDecisionGeneration == int.MaxValue ||
            (RequiresRegularV4ReceiveRecoveryLease(state) &&
             state.LastRecoveryLivenessLeaseGeneration == int.MaxValue))
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Rejected,
                "filetransfer_receive_recovery_authority_generation_exhausted");
        }

        var bridgeDecision = CreateBridgeRecoveryDecision(
            state,
            recoveryKind,
            observation.Reason,
            Math.Max(1, state.LastBridgeRecoveryDecisionGeneration + 1));
        var recoveryLease = RequiresRegularV4ReceiveRecoveryLease(state)
            ? IssueRecoveryLivenessLease(
                state,
                Math.Max(1, state.LastRecoveryLivenessLeaseGeneration + 1),
                RecoveryLivenessLeaseState.Active,
                observation.Reason)
            : null;
        if (bridgeDecision is not { } exactBridgeDecision ||
            (RequiresRegularV4ReceiveRecoveryLease(state) &&
             recoveryLease is null))
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Rejected,
                "filetransfer_receive_recovery_authority_incomplete");
        }

        var executionCommand = new FileTransferBridgeRecoveryExecutionCommand(
            exactBridgeDecision,
            recoveryLease,
            checkpointRequestId);
        var operationGeneration = Math.Max(
            1,
            recoveryState.LastOperationGeneration + 1);
        var compatibilityAuthority = new FileTransferBridgeRecoveryAuthority(
            observation.SessionId,
            observation.TransferId,
            observation.Direction,
            observation.RouteToken,
            observation.ProtocolVersion,
            observation.LiveRouteEpoch,
            observation.RuntimeOwner.RuntimeGeneration,
            observation.TerminalGeneration,
            observation.LegGeneration,
            observation.TransportEpoch,
            observation.BridgeRecoveryGeneration,
            exactBridgeDecision.BridgeDecisionGeneration,
            exactBridgeDecision.RecoveryKind.ToString(),
            NormalizeReason(observation.Reason));
        var authority = new FileTransferReceiveRecoveryOperationAuthority(
            observation,
            exactBridgeDecision,
            compatibilityAuthority,
            recoveryLease,
            executionCommand,
            operationGeneration,
            IssueReceiveRecoveryOperationAuthorityId(
                state,
                operationGeneration))
        {
            PredecessorAuthorityId =
                completedPredecessor?.Authority.AuthorityId,
            PredecessorOperationGeneration =
                completedPredecessor?.Authority.OperationGeneration ??
                0,
        };
        var issuanceState = completedPredecessor is { } predecessor
            ? ApplyCompletedReceiveRecoverySupersession(
                state,
                predecessor)
            : state;
        var issuedState = ApplyIssuedReceiveRecoveryOperation(
            issuanceState,
            authority);
        var holdReconciliation =
            ReconcileRuntimeUnlockInitialActivationHold(
                issuedState,
                completedPredecessor?.Authority,
                completedPredecessor is null
                    ? "filetransfer_receive_recovery_operation_issued"
                    : "filetransfer_receive_recovery_operation_successor_issued");
        return Decision(
            FileTransferReceiveRecoveryTransitionStatus.Accepted,
            completedPredecessor is null
                ? "filetransfer_receive_recovery_operation_issued"
                : "filetransfer_receive_recovery_operation_successor_issued",
            authority,
            holdReconciliation.State) with
        {
            InitialActivationHoldDecision =
                holdReconciliation.Decision,
        };
    }

    private static bool CanSupersedeCompletedReceiveRecoveryOperation(
        FileTransferCoordinatorState state,
        FileTransferReceiveRecoveryOperation predecessor,
        FileTransferReceiveRecoveryObservation observation,
        BridgeRecoveryKind recoveryKind,
        string? checkpointRequestId)
    {
        var retainedUnproven =
            predecessor.ClosureState ==
                FileTransferReceiveRecoveryClosureState.Retained &&
            predecessor.ReceiveProof is null;
        var adoptedProven =
            predecessor.ClosureState ==
                FileTransferReceiveRecoveryClosureState.Adopted &&
            predecessor.ReceiveProof is
                {
                    IsDefault: false,
                } receiveProof &&
            receiveProof.Authority == predecessor.Authority;
        var proofObservedUtcMs = adoptedProven
            ? predecessor.ReceiveProof!.Value.ObservedUtcMs
            : 0;
        if (predecessor.ExecutionState !=
                FileTransferReceiveRecoveryExecutionState.Completed ||
            predecessor.ExecutionReceipt is not
            {
                Outcome:
                    FileTransferBridgeRecoveryExecutionOutcome.Completed,
            } receipt ||
            receipt.Command != predecessor.Authority.ExecutionCommand ||
            predecessor.ExecutionReceiptObservedUtcMs <= 0 ||
            (!retainedUnproven && !adoptedProven) ||
            predecessor.ClosureGeneration <= 0 ||
            predecessor.HeldRetryRequest is not null ||
            predecessor.ReservedRetryRequest is not null ||
            predecessor.ReservedRetryAuthority is not null ||
            predecessor.ConsumingInstallReceipt is not null ||
            predecessor.Authority.RecoveryLivenessLease is not
                { State: RecoveryLivenessLeaseState.Active } activeLease ||
            predecessor.RetiredRecoveryLivenessLease is not { } retiredLease ||
            retiredLease != (activeLease with
            {
                State = RecoveryLivenessLeaseState.Retired,
                Reason = NormalizeReason(receipt.Reason),
            }) ||
            predecessor.Authority.BridgeRecoveryDecision.RecoveryKind !=
                recoveryKind ||
            recoveryKind != BridgeRecoveryKind.BridgeReceiveStallRecovery ||
            !string.Equals(
                predecessor.Authority.ExecutionCommand.CheckpointRequestId,
                checkpointRequestId,
                StringComparison.Ordinal) ||
            observation.HealthEvaluationEvidence is not
                { IsDefault: false } healthEvidence ||
            healthEvidence.Kind is not (
                FileTransferBridgeRecoveryEvaluationKind.PingTimeout or
                FileTransferBridgeRecoveryEvaluationKind.ReceiveStall or
                FileTransferBridgeRecoveryEvaluationKind
                    .SessionLivenessTimeout) ||
            healthEvidence.ObservedUtcMs <=
                predecessor.ExecutionReceiptObservedUtcMs ||
            healthEvidence.ObservedUtcMs <= proofObservedUtcMs ||
            observation.ObservedUtcMs != healthEvidence.ObservedUtcMs ||
            observation.ObservationGeneration <=
                predecessor.Authority.Observation.ObservationGeneration ||
            healthEvidence.EvidenceGeneration <=
                (predecessor.Authority.Observation.HealthEvaluationEvidence
                    ?.EvidenceGeneration ?? 0) ||
            !string.Equals(
                observation.Source,
                healthEvidence.ObservationKind,
                StringComparison.Ordinal) ||
            !string.Equals(
                observation.Reason,
                healthEvidence.Reason,
                StringComparison.Ordinal) ||
            !ReceiveRecoveryObservationScopeEquals(
                predecessor.Authority.Observation,
                observation) ||
            !TryValidateReceiveRecoveryObservationCurrent(
                state,
                observation,
                checkpointRequestId,
                out _))
        {
            return false;
        }

        return true;
    }

    private static bool ReceiveRecoveryObservationScopeEquals(
        FileTransferReceiveRecoveryObservation left,
        FileTransferReceiveRecoveryObservation right)
        => string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal) &&
           string.Equals(left.TransferId, right.TransferId, StringComparison.Ordinal) &&
           left.Direction == right.Direction &&
           left.Route == right.Route &&
           string.Equals(left.RouteToken, right.RouteToken, StringComparison.Ordinal) &&
           left.ProtocolVersion == right.ProtocolVersion &&
           left.LiveRouteEpoch == right.LiveRouteEpoch &&
           left.RuntimeOwner == right.RuntimeOwner &&
           left.RoutePublication == right.RoutePublication &&
           left.RouteCommit == right.RouteCommit &&
           left.TerminalGeneration == right.TerminalGeneration &&
           left.LegGeneration == right.LegGeneration &&
           left.TransportEpoch == right.TransportEpoch &&
           left.BridgeRecoveryGeneration == right.BridgeRecoveryGeneration &&
           string.Equals(
               left.CheckpointRequestId,
               right.CheckpointRequestId,
               StringComparison.Ordinal) &&
           string.Equals(
               left.DataSessionIdentity,
               right.DataSessionIdentity,
               StringComparison.Ordinal) &&
           left.DataSessionGeneration == right.DataSessionGeneration;

    private static FileTransferCoordinatorState
        ApplyCompletedReceiveRecoverySupersession(
            FileTransferCoordinatorState state,
            FileTransferReceiveRecoveryOperation predecessor)
    {
        var recoveryState = state.PersistentState.FileTransferReceiveRecovery;
        var invalidated = predecessor with
        {
            ExecutionState =
                FileTransferReceiveRecoveryExecutionState.Invalidated,
            ClosureState = FileTransferReceiveRecoveryClosureState.None,
            HeldRetryRequest = null,
            ReservedRetryRequest = null,
            ReservedRetryAuthority = null,
            ConsumingInstallReceipt = null,
            InvalidationReason =
                predecessor.ReceiveProof is null
                    ? "completed_unproven_receive_recovery_superseded_by_newer_health_observation"
                    : "completed_proven_unreserved_receive_recovery_superseded_by_newer_health_observation",
            LivenessDeferralDeadlineUtcMs = 0,
            LivenessDeferralGrant = null,
        };
        return state with
        {
            PersistentState = state.PersistentState with
            {
                FileTransferReceiveRecovery = recoveryState with
                {
                    CurrentOperation = null,
                    LastTerminalOperation = invalidated,
                    ProofStatePublication = null,
                },
            },
        };
    }

    internal static FileTransferReceiveRecoverySubstate
        GetReceiveRecoverySubstate(FileTransferCoordinatorState state)
        => state.PersistentState.FileTransferReceiveRecovery;

    internal static
        RuntimeUnlockInitialActivationReceiveRecoveryHoldCoordinatorDecision
        EvaluateRuntimeUnlockInitialActivationReceiveRecoveryHold(
            FileTransferCoordinatorState state,
            RuntimeUnlockInitialActivationReceiveRecoveryHoldRequest request)
    {
        RuntimeUnlockInitialActivationReceiveRecoveryHoldCoordinatorDecision
            Decision(
                RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus status,
                string reason,
                long holdGeneration = 0,
                FileTransferReceiveRecoveryOperation? operation = null,
                FileTransferReceiveRecoveryOperationAuthority?
                    previousAuthority = null,
                FileTransferCoordinatorState? nextState = null)
            => new(
                new RuntimeUnlockInitialActivationReceiveRecoveryHoldDecision(
                    request,
                    status,
                    NormalizeReason(reason),
                    holdGeneration,
                    operation,
                    previousAuthority))
            {
                State = nextState ?? state,
            };

        if (request.IsDefault)
        {
            return Decision(
                RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Rejected,
                "runtime_unlock_initial_activation_receive_recovery_hold_request_invalid");
        }

        if (!state.IsTerminal &&
            state.PersistentState.FileTransferReceiveRecovery
                .CurrentOperation is not null &&
            request.ExpectedScope.ExpectedExecutionCommand is null)
        {
            return Decision(
                RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Rejected,
                "runtime_unlock_initial_activation_receive_recovery_command_capture_race");
        }

        if (!RuntimeUnlockInitialActivationHoldRequestMatchesState(
                state,
                request))
        {
            return Decision(
                state.IsTerminal
                    ? RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus
                        .Invalidated
                    : RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus
                        .Rejected,
                state.IsTerminal
                    ? "runtime_unlock_initial_activation_receive_recovery_terminal"
                    : "runtime_unlock_initial_activation_receive_recovery_scope_mismatch");
        }

        var recoveryState =
            state.PersistentState.FileTransferReceiveRecovery;
        if (recoveryState.CurrentInitialActivationHold is { } currentHold)
        {
            if (currentHold.Request == request)
            {
                var reconciliation =
                    ReconcileRuntimeUnlockInitialActivationHold(
                        state,
                        currentHold.ReceiveRecoveryAuthority,
                        "runtime_unlock_initial_activation_receive_recovery_hold_rechecked");
                if (reconciliation.Decision is { } exactDecision)
                {
                    if (TryGetDirectCompletedReceiveRecoverySuccessor(
                            reconciliation.State,
                            request,
                            out _,
                            out var duplicatePredecessor))
                    {
                        exactDecision = exactDecision with
                        {
                            PreviousReceiveRecoveryAuthority =
                                duplicatePredecessor.Authority,
                        };
                    }

                    return new(exactDecision)
                    {
                        State = reconciliation.State,
                    };
                }

                return Decision(
                    RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Held,
                    currentHold.Reason,
                    currentHold.HoldGeneration,
                    recoveryState.CurrentOperation);
            }

            return Decision(
                RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Rejected,
                RuntimeUnlockInitialActivationHoldRequestHasSameIdentity(
                    currentHold.Request,
                    request)
                    ? "runtime_unlock_initial_activation_receive_recovery_hold_tuple_mutated"
                    : "runtime_unlock_initial_activation_receive_recovery_hold_conflict",
                currentHold.HoldGeneration,
                recoveryState.CurrentOperation);
        }

        var currentOperation = recoveryState.CurrentOperation;
        var capturedPredecessorRebind =
            TryGetDirectCompletedReceiveRecoverySuccessor(
                state,
                request,
                out var capturedSuccessor,
                out var capturedPredecessor) &&
            currentOperation is { } exactCurrentOperation &&
            capturedSuccessor.Authority == exactCurrentOperation.Authority;
        if (currentOperation is null)
        {
            var terminalGate = recoveryState.LastInitialActivationHold;
            if (terminalGate is { } exactTerminalGate &&
                exactTerminalGate.Request == request)
            {
                var exactTerminalOperation =
                    FindReceiveRecoveryOperation(
                        recoveryState,
                        exactTerminalGate.ReceiveRecoveryAuthority);
                return Decision(
                    exactTerminalGate.Status,
                    exactTerminalGate.Reason,
                    exactTerminalGate.HoldGeneration,
                    exactTerminalOperation);
            }

            return Decision(
                RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Released,
                "runtime_unlock_initial_activation_receive_recovery_not_blocked");
        }

        var lastGate = recoveryState.LastInitialActivationHold;
        if (lastGate is { } terminalHold &&
            terminalHold.Request == request &&
            terminalHold.ReceiveRecoveryAuthority ==
                currentOperation.Value.Authority)
        {
            return Decision(
                terminalHold.Status,
                terminalHold.Reason,
                terminalHold.HoldGeneration,
                currentOperation);
        }

        if (!ReceiveRecoveryOperationMatchesInitialActivationRequest(
                currentOperation.Value,
                request,
                matchExecutionCommand: !capturedPredecessorRebind) ||
            !TryValidateReceiveRecoveryObservationCurrent(
                state,
                currentOperation.Value.Authority.Observation,
                currentOperation.Value.Authority.ExecutionCommand
                    .CheckpointRequestId,
                out _))
        {
            return Decision(
                RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Rejected,
                "runtime_unlock_initial_activation_receive_recovery_operation_scope_mismatch",
                operation: currentOperation);
        }

        var operationStatus =
            GetRuntimeUnlockInitialActivationReceiveRecoveryHoldStatus(
                currentOperation.Value);
        if (operationStatus !=
            RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Held)
        {
            return Decision(
                operationStatus,
                operationStatus ==
                    RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus
                        .Released
                    ? "runtime_unlock_initial_activation_receive_recovery_proof_current"
                    : "runtime_unlock_initial_activation_receive_recovery_terminal",
                operation: currentOperation,
                previousAuthority: capturedPredecessorRebind
                    ? capturedPredecessor.Authority
                    : null);
        }

        if (recoveryState.LastInitialActivationHoldGeneration ==
            long.MaxValue)
        {
            return Decision(
                RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Rejected,
                "runtime_unlock_initial_activation_receive_recovery_hold_generation_exhausted",
                operation: currentOperation);
        }

        var holdGeneration = Math.Max(
            1,
            recoveryState.LastInitialActivationHoldGeneration + 1);
        var hold = new RuntimeUnlockInitialActivationReceiveRecoveryHold(
            request,
            currentOperation.Value.Authority,
            holdGeneration,
            RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Held,
            capturedPredecessorRebind
                ? "runtime_unlock_initial_activation_receive_recovery_hold_rebound"
                : "runtime_unlock_initial_activation_receive_recovery_operation_current");
        var heldState = ApplyReceiveRecoverySubstate(
            state,
            recoveryState with
            {
                CurrentInitialActivationHold = hold,
                LastInitialActivationHoldGeneration = holdGeneration,
            });
        return Decision(
            RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Held,
            hold.Reason,
            holdGeneration,
            currentOperation,
            capturedPredecessorRebind
                ? capturedPredecessor.Authority
                : null,
            nextState: heldState);
    }

    internal static
        RuntimeUnlockInitialActivationReceiveRecoveryHoldCoordinatorDecision
        InvalidateRuntimeUnlockInitialActivationReceiveRecoveryHold(
            FileTransferCoordinatorState state,
            RuntimeUnlockInitialActivationReceiveRecoveryHoldInvalidationRequest
                request)
    {
        RuntimeUnlockInitialActivationReceiveRecoveryHoldCoordinatorDecision
            Decision(
                RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus status,
                string reason,
                long holdGeneration = 0,
                FileTransferReceiveRecoveryOperation? operation = null,
                FileTransferCoordinatorState? nextState = null)
            => new(
                new RuntimeUnlockInitialActivationReceiveRecoveryHoldDecision(
                    request.HoldRequest,
                    status,
                    NormalizeReason(reason),
                    holdGeneration,
                    operation))
            {
                State = nextState ?? state,
            };

        if (request.IsDefault)
        {
            return Decision(
                RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Rejected,
                "runtime_unlock_initial_activation_receive_recovery_hold_invalidation_request_invalid");
        }

        var recoveryState =
            state.PersistentState.FileTransferReceiveRecovery;
        var normalizedReason = NormalizeReason(request.Reason);
        if (recoveryState.CurrentInitialActivationHold is not
            { } currentHold)
        {
            if (recoveryState.LastInitialActivationHold is
                    { } terminalHold &&
                terminalHold.Request == request.HoldRequest &&
                (terminalHold.Status ==
                     RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus
                         .Released ||
                 terminalHold.Status ==
                     RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus
                         .Invalidated &&
                 terminalHold.InvalidationKind == request.Kind &&
                 string.Equals(
                     terminalHold.Reason,
                     normalizedReason,
                     StringComparison.Ordinal)))
            {
                return Decision(
                    terminalHold.Status,
                    terminalHold.Reason,
                    terminalHold.HoldGeneration,
                    FindReceiveRecoveryOperation(
                        recoveryState,
                        terminalHold.ReceiveRecoveryAuthority));
            }

            return Decision(
                RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Rejected,
                "runtime_unlock_initial_activation_receive_recovery_hold_not_current");
        }

        if (currentHold.Request != request.HoldRequest)
        {
            return Decision(
                RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Rejected,
                RuntimeUnlockInitialActivationHoldRequestHasSameIdentity(
                    currentHold.Request,
                    request.HoldRequest)
                    ? "runtime_unlock_initial_activation_receive_recovery_hold_tuple_mutated"
                    : "runtime_unlock_initial_activation_receive_recovery_hold_conflict",
                currentHold.HoldGeneration,
                FindReceiveRecoveryOperation(
                    recoveryState,
                    currentHold.ReceiveRecoveryAuthority));
        }

        if (request.Kind is not (
                RuntimeUnlockInitialActivationReceiveRecoveryHoldInvalidationKind
                    .UserCancellation or
                RuntimeUnlockInitialActivationReceiveRecoveryHoldInvalidationKind
                    .PeerCancellation))
        {
            return Decision(
                RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Rejected,
                "runtime_unlock_initial_activation_receive_recovery_hold_invalidation_not_authorized",
                currentHold.HoldGeneration,
                FindReceiveRecoveryOperation(
                    recoveryState,
                    currentHold.ReceiveRecoveryAuthority));
        }

        var invalidatedHold = currentHold with
        {
            Status =
                RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus
                    .Invalidated,
            Reason = normalizedReason,
            InvalidationKind = request.Kind,
        };
        var nextState = ApplyReceiveRecoverySubstate(
            state,
            recoveryState with
            {
                CurrentInitialActivationHold = null,
                LastInitialActivationHold = invalidatedHold,
            });
        return Decision(
            invalidatedHold.Status,
            invalidatedHold.Reason,
            invalidatedHold.HoldGeneration,
            FindReceiveRecoveryOperation(
                recoveryState,
                invalidatedHold.ReceiveRecoveryAuthority),
            nextState);
    }

    private static bool
        RuntimeUnlockInitialActivationHoldRequestMatchesState(
            FileTransferCoordinatorState state,
            RuntimeUnlockInitialActivationReceiveRecoveryHoldRequest request)
    {
        if (state.IsTerminal ||
            !string.Equals(
                request.SessionId,
                state.SessionId,
                StringComparison.Ordinal) ||
            !string.Equals(
                request.TransferId,
                state.TransferId,
                StringComparison.Ordinal) ||
            request.Direction != state.Direction ||
            state.CurrentRuntimeOwner is not { IsDefault: false } ||
            state.CurrentRoutePublicationToken is not
                { IsDefault: false } publication ||
            state.CurrentLeg is not { } leg ||
            string.IsNullOrWhiteSpace(state.CurrentDataSessionIdentity) ||
            state.CurrentDataSessionGeneration <= 0)
        {
            return false;
        }

        var expected = request.ExpectedScope;
        if (expected.IsDefault ||
            expected.SourcePublicationAuthority is not { } sourcePublication ||
            !MatchesRoutePublicationAuthority(
                sourcePublication,
                publication) ||
            expected.TransportEpoch != leg.TransportEpochId ||
            expected.BridgeRecoveryGeneration !=
                leg.BridgeRecoveryGeneration ||
            !string.Equals(
                expected.CheckpointRequestId,
                leg.CheckpointRequestId,
                StringComparison.Ordinal) ||
            !string.Equals(
                expected.DataSessionIdentity,
                state.CurrentDataSessionIdentity,
                StringComparison.Ordinal) ||
            expected.DataSessionGeneration !=
                state.CurrentDataSessionGeneration)
        {
            return false;
        }

        var recoveryState =
            state.PersistentState.FileTransferReceiveRecovery;
        if (recoveryState.CurrentOperation is { } current)
        {
            if (expected.ExpectedExecutionCommand is null)
            {
                return false;
            }

            var commandMatches =
                expected.ExpectedExecutionCommand ==
                    current.Authority.ExecutionCommand;
            if (!commandMatches)
            {
                if (TryGetDirectCompletedReceiveRecoverySuccessor(
                        state,
                        request,
                        out var capturedSuccessor,
                        out _) &&
                    capturedSuccessor.Authority == current.Authority)
                {
                    return true;
                }

                var retainedGate =
                    recoveryState.CurrentInitialActivationHold;
                if (retainedGate is not { } retained ||
                    retained.Request != request ||
                    retained.ReceiveRecoveryAuthority != current.Authority)
                {
                    return false;
                }
            }

            return ReceiveRecoveryOperationMatchesExpectedScope(
                current,
                expected,
                matchExecutionCommand: commandMatches);
        }

        if (expected.ExpectedExecutionCommand is not { } terminalCommand)
        {
            return true;
        }

        return recoveryState.LastTerminalOperation is { } terminal &&
               terminal.Authority.ExecutionCommand == terminalCommand &&
               ReceiveRecoveryOperationMatchesExpectedScope(
                   terminal,
                   expected);
    }

    private static bool
        TryGetDirectCompletedReceiveRecoverySuccessor(
            FileTransferCoordinatorState state,
            RuntimeUnlockInitialActivationReceiveRecoveryHoldRequest request,
            out FileTransferReceiveRecoveryOperation successor,
            out FileTransferReceiveRecoveryOperation predecessor)
    {
        successor = default;
        predecessor = default;
        var recoveryState =
            state.PersistentState.FileTransferReceiveRecovery;
        if (recoveryState.CurrentOperation is not { } exactSuccessor ||
            recoveryState.LastTerminalOperation is not { } exactPredecessor ||
            exactPredecessor.ExecutionState !=
                FileTransferReceiveRecoveryExecutionState.Invalidated ||
            exactPredecessor.ExecutionReceipt is not
            {
                Outcome:
                    FileTransferBridgeRecoveryExecutionOutcome.Completed,
            } predecessorReceipt ||
            predecessorReceipt.Command !=
                exactPredecessor.Authority.ExecutionCommand ||
            exactPredecessor.ExecutionReceiptObservedUtcMs <= 0 ||
            exactPredecessor.Authority.RecoveryLivenessLease is not
                { State: RecoveryLivenessLeaseState.Active }
                    predecessorActiveLease ||
            exactPredecessor.RetiredRecoveryLivenessLease is not
                { } predecessorRetiredLease ||
            predecessorRetiredLease != (predecessorActiveLease with
            {
                State = RecoveryLivenessLeaseState.Retired,
                Reason = NormalizeReason(predecessorReceipt.Reason),
            }) ||
            exactPredecessor.ClosureState !=
                FileTransferReceiveRecoveryClosureState.None ||
            exactPredecessor.HeldRetryRequest is not null ||
            exactPredecessor.ReservedRetryRequest is not null ||
            exactPredecessor.ReservedRetryAuthority is not null ||
            exactPredecessor.ConsumingInstallReceipt is not null ||
            !IsCompletedReceiveRecoverySuccessorInvalidation(
                exactPredecessor) ||
            request.ExpectedScope.ExpectedExecutionCommand !=
                exactPredecessor.Authority.ExecutionCommand ||
            exactPredecessor.Authority.OperationGeneration == long.MaxValue ||
            exactSuccessor.Authority.OperationGeneration !=
                exactPredecessor.Authority.OperationGeneration + 1 ||
            exactSuccessor.Authority.PredecessorAuthorityId !=
                exactPredecessor.Authority.AuthorityId ||
            exactSuccessor.Authority.PredecessorOperationGeneration !=
                exactPredecessor.Authority.OperationGeneration ||
            recoveryState.LastOperationGeneration !=
                exactSuccessor.Authority.OperationGeneration ||
            !ReceiveRecoveryObservationScopeEquals(
                exactPredecessor.Authority.Observation,
                exactSuccessor.Authority.Observation) ||
            !ReceiveRecoveryOperationMatchesInitialActivationRequest(
                exactPredecessor,
                request) ||
            !ReceiveRecoveryOperationMatchesInitialActivationRequest(
                exactSuccessor,
                request,
                matchExecutionCommand: false) ||
            !TryValidateReceiveRecoveryObservationCurrent(
                state,
                exactSuccessor.Authority.Observation,
                exactSuccessor.Authority.ExecutionCommand
                    .CheckpointRequestId,
                out _))
        {
            return false;
        }

        successor = exactSuccessor;
        predecessor = exactPredecessor;
        return true;
    }

    private static bool IsCompletedReceiveRecoverySuccessorInvalidation(
        FileTransferReceiveRecoveryOperation predecessor)
        => predecessor.ReceiveProof switch
        {
            null => string.Equals(
                predecessor.InvalidationReason,
                "completed_unproven_receive_recovery_superseded_by_newer_health_observation",
                StringComparison.Ordinal),
            { IsDefault: false } proof
                when proof.Authority == predecessor.Authority =>
                string.Equals(
                    predecessor.InvalidationReason,
                    "completed_proven_unreserved_receive_recovery_superseded_by_newer_health_observation",
                    StringComparison.Ordinal),
            _ => false,
        };

    private static bool
        RuntimeUnlockInitialActivationHoldRequestHasSameIdentity(
            RuntimeUnlockInitialActivationReceiveRecoveryHoldRequest left,
            RuntimeUnlockInitialActivationReceiveRecoveryHoldRequest right)
        => string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal) &&
           string.Equals(left.TransferId, right.TransferId, StringComparison.Ordinal) &&
           left.Direction == right.Direction &&
           left.PayerDecisionId == right.PayerDecisionId &&
           left.OfferGeneration == right.OfferGeneration;

    private static bool
        ReceiveRecoveryOperationMatchesInitialActivationRequest(
            FileTransferReceiveRecoveryOperation operation,
            RuntimeUnlockInitialActivationReceiveRecoveryHoldRequest request,
            bool matchExecutionCommand = true)
    {
        var observation = operation.Authority.Observation;
        return string.Equals(
                   request.SessionId,
                   observation.SessionId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   request.TransferId,
                   observation.TransferId,
                   StringComparison.Ordinal) &&
               request.Direction == observation.Direction &&
               ReceiveRecoveryOperationMatchesExpectedScope(
                   operation,
                   request.ExpectedScope,
                   matchExecutionCommand);
    }

    private static bool ReceiveRecoveryOperationMatchesExpectedScope(
        FileTransferReceiveRecoveryOperation operation,
        RuntimeUnlockInitialActivationReceiveRecoveryExpectedScope expected,
        bool matchExecutionCommand = true)
    {
        var observation = operation.Authority.Observation;
        return expected.SourcePublicationAuthority is { } publication &&
               MatchesRoutePublicationAuthority(
                   publication,
                   observation.RoutePublication) &&
               expected.TransportEpoch == observation.TransportEpoch &&
               expected.BridgeRecoveryGeneration ==
                   observation.BridgeRecoveryGeneration &&
               string.Equals(
                   expected.CheckpointRequestId,
                   observation.CheckpointRequestId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   expected.DataSessionIdentity,
                   observation.DataSessionIdentity,
                   StringComparison.Ordinal) &&
               expected.DataSessionGeneration ==
                   observation.DataSessionGeneration &&
               (!matchExecutionCommand ||
                expected.ExpectedExecutionCommand is null ||
                expected.ExpectedExecutionCommand ==
                    operation.Authority.ExecutionCommand);
    }

    private static RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus
        GetRuntimeUnlockInitialActivationReceiveRecoveryHoldStatus(
            FileTransferReceiveRecoveryOperation operation)
        => operation switch
        {
            {
                ExecutionState:
                    FileTransferReceiveRecoveryExecutionState.Issued or
                    FileTransferReceiveRecoveryExecutionState.Running,
            } =>
                RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Held,
            {
                ExecutionState:
                    FileTransferReceiveRecoveryExecutionState.Completed,
                ClosureState:
                    FileTransferReceiveRecoveryClosureState.Adopted or
                    FileTransferReceiveRecoveryClosureState.Reserved or
                    FileTransferReceiveRecoveryClosureState.Consumed,
            } =>
                RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Released,
            {
                ExecutionState:
                    FileTransferReceiveRecoveryExecutionState.Completed,
                ClosureState:
                    FileTransferReceiveRecoveryClosureState.Retained,
                ReceiveProof: null,
            } =>
                RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Held,
            _ =>
                RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus
                    .Invalidated,
        };

    private static FileTransferReceiveRecoveryOperation?
        FindReceiveRecoveryOperation(
            FileTransferReceiveRecoverySubstate recoveryState,
            FileTransferReceiveRecoveryOperationAuthority authority)
    {
        if (recoveryState.CurrentOperation is { } current &&
            current.Authority == authority)
        {
            return current;
        }

        return recoveryState.LastTerminalOperation is { } terminal &&
               terminal.Authority == authority
            ? terminal
            : null;
    }

    private static (
        FileTransferCoordinatorState State,
        RuntimeUnlockInitialActivationReceiveRecoveryHoldDecision? Decision)
        ReconcileRuntimeUnlockInitialActivationHold(
            FileTransferCoordinatorState state,
            FileTransferReceiveRecoveryOperationAuthority?
                previousAuthority,
            string reason)
    {
        var recoveryState =
            state.PersistentState.FileTransferReceiveRecovery;
        var hold = recoveryState.CurrentInitialActivationHold;
        if (hold is not { } activeHold)
        {
            return (state, null);
        }

        var normalizedReason = NormalizeReason(reason);
        if (recoveryState.CurrentOperation is { } current &&
            current.Authority == activeHold.ReceiveRecoveryAuthority)
        {
            var status =
                TryValidateReceiveRecoveryObservationCurrent(
                    state,
                    current.Authority.Observation,
                    current.Authority.ExecutionCommand.CheckpointRequestId,
                    out _)
                    ? GetRuntimeUnlockInitialActivationReceiveRecoveryHoldStatus(
                        current)
                    : RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus
                        .Invalidated;
            if (status ==
                RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Held)
            {
                return (
                    state,
                    new RuntimeUnlockInitialActivationReceiveRecoveryHoldDecision(
                        activeHold.Request,
                        status,
                        activeHold.Reason,
                        activeHold.HoldGeneration,
                        current));
            }

            var currentTerminalReason = status ==
                RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Released
                    ? "runtime_unlock_initial_activation_receive_recovery_proof_current"
                    : normalizedReason;
            var terminalHold = activeHold with
            {
                Status = status,
                Reason = currentTerminalReason,
            };
            var nextRecoveryState = recoveryState with
            {
                CurrentInitialActivationHold = null,
                LastInitialActivationHold = terminalHold,
            };
            var nextState = ApplyReceiveRecoverySubstate(
                state,
                nextRecoveryState);
            RuntimeUnlockInitialActivationOfferEnvelopeRefreshCommand?
                refreshCommand = null;
            if (status ==
                    RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus
                        .Released &&
                terminalHold.Request.EnvelopeGeneration > 0 &&
                !string.IsNullOrWhiteSpace(
                    terminalHold.Request.SemanticOfferFingerprint))
            {
                (nextState, refreshCommand) =
                    StageRuntimeUnlockInitialActivationOfferEnvelopeRefresh(
                        nextState,
                        terminalHold,
                        current);
            }
            return (
                nextState,
                new RuntimeUnlockInitialActivationReceiveRecoveryHoldDecision(
                    activeHold.Request,
                    status,
                    currentTerminalReason,
                    activeHold.HoldGeneration,
                    current)
                {
                    EnvelopeRefreshCommand = refreshCommand,
                });
        }

        if (recoveryState.CurrentOperation is { } successor &&
            previousAuthority is { } predecessor &&
            predecessor == activeHold.ReceiveRecoveryAuthority &&
            successor.Authority.PredecessorAuthorityId ==
                predecessor.AuthorityId &&
            successor.Authority.PredecessorOperationGeneration ==
                predecessor.OperationGeneration &&
            ReceiveRecoveryOperationMatchesInitialActivationRequest(
                successor,
                activeHold.Request,
                matchExecutionCommand: false))
        {
            var reboundReason =
                "runtime_unlock_initial_activation_receive_recovery_hold_rebound";
            var rebound = activeHold with
            {
                ReceiveRecoveryAuthority = successor.Authority,
                Status =
                    RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus
                        .Held,
                Reason = reboundReason,
            };
            var nextState = ApplyReceiveRecoverySubstate(
                state,
                recoveryState with
                {
                    CurrentInitialActivationHold = rebound,
                });
            return (
                nextState,
                new RuntimeUnlockInitialActivationReceiveRecoveryHoldDecision(
                    activeHold.Request,
                    RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus
                        .Held,
                    reboundReason,
                    activeHold.HoldGeneration,
                    successor,
                    predecessor));
        }

        var retainedTerminal = recoveryState.LastTerminalOperation;
        var terminalStatus =
            retainedTerminal is { } terminal &&
            terminal.Authority == activeHold.ReceiveRecoveryAuthority &&
            GetRuntimeUnlockInitialActivationReceiveRecoveryHoldStatus(
                terminal) ==
                RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Released
                ? RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus
                    .Released
                : RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus
                    .Invalidated;
        var terminalReason = terminalStatus ==
            RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Released
                ? "runtime_unlock_initial_activation_receive_recovery_proof_current"
                : normalizedReason;
        var completedHold = activeHold with
        {
            Status = terminalStatus,
            Reason = terminalReason,
        };
        var invalidatedState = ApplyReceiveRecoverySubstate(
            state,
            recoveryState with
            {
                CurrentInitialActivationHold = null,
                LastInitialActivationHold = completedHold,
            });
        RuntimeUnlockInitialActivationOfferEnvelopeRefreshCommand?
            retainedRefreshCommand = null;
        if (terminalStatus ==
                RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus
                    .Released &&
            retainedTerminal is { } exactRetainedTerminal)
        {
            if (completedHold.Request.EnvelopeGeneration > 0 &&
                !string.IsNullOrWhiteSpace(
                    completedHold.Request.SemanticOfferFingerprint))
            {
                (invalidatedState, retainedRefreshCommand) =
                    StageRuntimeUnlockInitialActivationOfferEnvelopeRefresh(
                        invalidatedState,
                        completedHold,
                        exactRetainedTerminal);
            }
        }
        return (
            invalidatedState,
            new RuntimeUnlockInitialActivationReceiveRecoveryHoldDecision(
                activeHold.Request,
                terminalStatus,
                terminalReason,
                activeHold.HoldGeneration,
                retainedTerminal)
            {
                EnvelopeRefreshCommand = retainedRefreshCommand,
            });
    }

    private static (
        FileTransferCoordinatorState State,
        RuntimeUnlockInitialActivationOfferEnvelopeRefreshCommand? Command)
        StageRuntimeUnlockInitialActivationOfferEnvelopeRefresh(
            FileTransferCoordinatorState state,
            RuntimeUnlockInitialActivationReceiveRecoveryHold releasedHold,
            FileTransferReceiveRecoveryOperation operation)
    {
        var recoveryState =
            state.PersistentState.FileTransferReceiveRecovery;
        if (recoveryState.PendingInitialActivationEnvelopeRefresh is
                { } pending)
        {
            return pending.HoldRequest == releasedHold.Request &&
                   pending.HoldGeneration == releasedHold.HoldGeneration &&
                   pending.ReceiveRecoveryAuthority == operation.Authority &&
                   pending.ClosureGeneration == operation.ClosureGeneration
                ? (state, pending)
                : (state, null);
        }

        var request = releasedHold.Request;
        var envelopeLifetimeMs =
            request.EnvelopeExpiresAtUnixMs - request.EnvelopeSentAtUnixMs;
        if (releasedHold.Status !=
                RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus
                    .Released ||
            operation.ExecutionState !=
                FileTransferReceiveRecoveryExecutionState.Completed ||
            operation.ReceiveProof is null ||
            operation.ClosureState is not (
                FileTransferReceiveRecoveryClosureState.Adopted or
                FileTransferReceiveRecoveryClosureState.Reserved or
                FileTransferReceiveRecoveryClosureState.Consumed) ||
            operation.ClosureGeneration <= 0 ||
            string.IsNullOrWhiteSpace(request.SemanticOfferFingerprint) ||
            request.EnvelopeGeneration <= 0 ||
            request.EnvelopeSentAtUnixMs <= 0 ||
            envelopeLifetimeMs <= 0 ||
            recoveryState.LastInitialActivationEnvelopeRefreshGeneration ==
                long.MaxValue)
        {
            return (state, null);
        }

        var refreshGeneration =
            recoveryState.LastInitialActivationEnvelopeRefreshGeneration + 1;
        var sentAtUnixMs =
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var command =
            new RuntimeUnlockInitialActivationOfferEnvelopeRefreshCommand(
                request,
                releasedHold.HoldGeneration,
                operation.Authority,
                operation.ClosureGeneration,
                refreshGeneration,
                request.EnvelopeGeneration + 1,
                sentAtUnixMs,
                sentAtUnixMs + envelopeLifetimeMs);
        var nextState = ApplyReceiveRecoverySubstate(
            state,
            recoveryState with
            {
                PendingInitialActivationEnvelopeRefresh = command,
                LastInitialActivationEnvelopeRefreshGeneration =
                    refreshGeneration,
            });
        return (nextState, command);
    }

    internal static
        RuntimeUnlockInitialActivationOfferEnvelopeRefreshCoordinatorDecision
        CommitRuntimeUnlockInitialActivationOfferEnvelopeRefresh(
            FileTransferCoordinatorState state,
            RuntimeUnlockInitialActivationOfferEnvelopeRefreshReceipt receipt)
    {
        RuntimeUnlockInitialActivationOfferEnvelopeRefreshCoordinatorDecision
            Decision(
                RuntimeUnlockInitialActivationOfferEnvelopeRefreshStatus status,
                string reason,
                FileTransferCoordinatorState? nextState = null)
            => new(
                new RuntimeUnlockInitialActivationOfferEnvelopeRefreshDecision(
                    receipt,
                    status,
                    NormalizeReason(reason)))
            {
                State = nextState ?? state,
            };

        if (receipt.IsDefault)
        {
            return Decision(
                RuntimeUnlockInitialActivationOfferEnvelopeRefreshStatus
                    .Rejected,
                "runtime_unlock_initial_activation_offer_envelope_refresh_receipt_invalid");
        }

        var recoveryState =
            state.PersistentState.FileTransferReceiveRecovery;
        if (recoveryState.LastInitialActivationEnvelopeRefreshReceipt ==
            receipt)
        {
            return Decision(
                RuntimeUnlockInitialActivationOfferEnvelopeRefreshStatus
                    .Idempotent,
                "runtime_unlock_initial_activation_offer_envelope_refresh_receipt_idempotent");
        }

        if (recoveryState.PendingInitialActivationEnvelopeRefresh is not
                { } pending ||
            pending != receipt.Command)
        {
            return Decision(
                RuntimeUnlockInitialActivationOfferEnvelopeRefreshStatus
                    .Rejected,
                "runtime_unlock_initial_activation_offer_envelope_refresh_not_pending");
        }

        if (recoveryState.LastInitialActivationHold is not
                { Status:
                    RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus
                        .Released } released ||
            released.Request != pending.HoldRequest ||
            released.HoldGeneration != pending.HoldGeneration ||
            released.ReceiveRecoveryAuthority !=
                pending.ReceiveRecoveryAuthority ||
            !RuntimeUnlockInitialActivationHoldRequestMatchesState(
                state,
                pending.HoldRequest))
        {
            var invalidatedState = ApplyReceiveRecoverySubstate(
                state,
                recoveryState with
                {
                    PendingInitialActivationEnvelopeRefresh = null,
                });
            return Decision(
                RuntimeUnlockInitialActivationOfferEnvelopeRefreshStatus
                    .Invalidated,
                "runtime_unlock_initial_activation_offer_envelope_refresh_scope_not_current",
                invalidatedState);
        }

        var nextState = ApplyReceiveRecoverySubstate(
            state,
            recoveryState with
            {
                PendingInitialActivationEnvelopeRefresh = null,
                LastInitialActivationEnvelopeRefreshReceipt = receipt,
            });
        return Decision(
            RuntimeUnlockInitialActivationOfferEnvelopeRefreshStatus.Accepted,
            "runtime_unlock_initial_activation_offer_envelope_refresh_committed",
            nextState);
    }

    internal static FileTransferReceiveRecoveryOperationAuthority?
        GetCurrentReceiveRecoveryOperationAuthority(
            FileTransferCoordinatorState state)
        => state.PersistentState.FileTransferReceiveRecovery
            .CurrentOperation?.Authority;

    internal static bool IsReceiveRecoveryOperationCurrent(
        FileTransferCoordinatorState state,
        FileTransferReceiveRecoveryOperationAuthority authority)
        => state.PersistentState.FileTransferReceiveRecovery
               .CurrentOperation is { } current &&
           current.Authority == authority &&
           TryValidateReceiveRecoveryObservationCurrent(
               state,
               authority.Observation,
               authority.ExecutionCommand.CheckpointRequestId,
               out _);

    internal static bool TryGetCurrentReceiveRecoveryPayloadSubmissionHold(
        FileTransferCoordinatorState state,
        out FileTransferReceiveRecoveryOperation operation,
        out string reason)
    {
        operation = default;
        reason = "filetransfer_receive_recovery_execution_not_active";
        if (state.PersistentState.FileTransferReceiveRecovery
                .CurrentOperation is not { } current ||
            current.ExecutionState is not
                (FileTransferReceiveRecoveryExecutionState.Issued or
                 FileTransferReceiveRecoveryExecutionState.Running))
        {
            return false;
        }

        operation = current;
        if (!IsReceiveRecoveryOperationCurrent(
                state,
                current.Authority))
        {
            reason =
                "filetransfer_receive_recovery_execution_scope_not_current";
            operation = default;
            return false;
        }

        reason = "filetransfer_receive_recovery_execution_current";
        return true;
    }

    internal static bool TryGetReceiveRecoveryPayloadSubmissionHold(
        FileTransferCoordinatorState state,
        out FileTransferReceiveRecoveryOperation operation,
        out string reason)
    {
        if (TryGetCurrentReceiveRecoveryPayloadSubmissionHold(
                state,
                out operation,
                out reason))
        {
            return true;
        }

        operation = default;
        reason = "filetransfer_receive_recovery_execution_not_active";
        var recovery = state.PersistentState.FileTransferReceiveRecovery;
        if (recovery.CurrentOperation is { } current)
        {
            if (current.ExecutionState !=
                    FileTransferReceiveRecoveryExecutionState.Completed ||
                current.ExecutionReceipt is not
                {
                    Outcome:
                        FileTransferBridgeRecoveryExecutionOutcome.Completed,
                } ||
                current.ClosureState !=
                    FileTransferReceiveRecoveryClosureState.Retained ||
                current.ReceiveProof is not null ||
                !IsReceiveRecoveryOperationCurrent(
                    state,
                    current.Authority))
            {
                return false;
            }

            operation = current;
            reason =
                "filetransfer_receive_recovery_completed_awaiting_receive_proof";
            return true;
        }

        if (
            recovery.LastTerminalOperation is not
            {
                ExecutionState:
                    FileTransferReceiveRecoveryExecutionState.Exhausted or
                    FileTransferReceiveRecoveryExecutionState.Discarded or
                    FileTransferReceiveRecoveryExecutionState.Invalidated,
            } terminal)
        {
            return false;
        }

        if (!TryValidateReceiveRecoveryObservationCurrent(
                state,
                terminal.Authority.Observation,
                terminal.Authority.ExecutionCommand.CheckpointRequestId,
                out _))
        {
            reason =
                "filetransfer_receive_recovery_execution_scope_not_current";
            return false;
        }

        operation = terminal;
        reason =
            "filetransfer_receive_recovery_execution_terminal_without_completion";
        return true;
    }

    internal static bool
        TryGetReceiveRecoveryPayloadSubmissionProofRelease(
            FileTransferCoordinatorState state,
            out FileTransferReceiveRecoveryOperation operation)
    {
        operation = default;
        if (state.PersistentState.FileTransferReceiveRecovery
                .CurrentOperation is not
            {
                ExecutionState:
                    FileTransferReceiveRecoveryExecutionState.Completed,
                ExecutionReceipt:
                {
                    Outcome:
                        FileTransferBridgeRecoveryExecutionOutcome.Completed,
                },
                ReceiveProof: not null,
                ClosureState:
                    FileTransferReceiveRecoveryClosureState.Adopted or
                    FileTransferReceiveRecoveryClosureState.Reserved or
                    FileTransferReceiveRecoveryClosureState.Consumed,
            } current ||
            !IsReceiveRecoveryOperationCurrent(
                state,
                current.Authority))
        {
            return false;
        }

        operation = current;
        return true;
    }

    internal static bool TryGetReceiveRecoveryLivenessDeferral(
        FileTransferCoordinatorState state,
        out FileTransferReceiveRecoveryOperation operation,
        out FileTransferRecoveryLivenessDeferralGrant grant,
        out DateTimeOffset maximumDeadlineUtc)
    {
        operation = default;
        grant = default!;
        maximumDeadlineUtc = default;
        if (state.PersistentState.FileTransferReceiveRecovery
                .CurrentOperation is not { } current ||
            !IsReceiveRecoveryOperationCurrent(state, current.Authority) ||
            current.LivenessDeferralDeadlineUtcMs <= 0 ||
            current.LivenessDeferralGrant is not { } currentGrant ||
            currentGrant.LivenessDeferralDeadlineUtc.ToUnixTimeMilliseconds() !=
                current.LivenessDeferralDeadlineUtcMs)
        {
            return false;
        }

        if ((current.ExecutionState is
                 FileTransferReceiveRecoveryExecutionState.Issued or
                 FileTransferReceiveRecoveryExecutionState.Running) &&
            (current.Authority.RecoveryLivenessLease is null ||
             current.Authority.RecoveryLivenessLease is
                 { State: RecoveryLivenessLeaseState.Active }) &&
            currentGrant ==
                CreateReceiveRecoveryLivenessDeferralGrant(
                    current.Authority,
                    current.LivenessDeferralDeadlineUtcMs))
        {
            operation = current;
            grant = currentGrant;
            maximumDeadlineUtc = DateTimeOffset.FromUnixTimeMilliseconds(
                current.LivenessDeferralDeadlineUtcMs);
            return true;
        }

        if (current.ExecutionState !=
                FileTransferReceiveRecoveryExecutionState.Completed ||
            current.ReceiveProof is not null ||
            current.ExecutionReceipt is not
                {
                    Outcome:
                        FileTransferBridgeRecoveryExecutionOutcome.Completed,
                } completedReceipt)
        {
            return false;
        }

        if (current.Authority.RecoveryLivenessLease is null)
        {
            if (current.ClosureState !=
                    FileTransferReceiveRecoveryClosureState.None ||
                current.RetiredRecoveryLivenessLease is not null ||
                currentGrant !=
                    CreateReceiveRecoveryLivenessDeferralGrant(
                        current.Authority,
                        current.LivenessDeferralDeadlineUtcMs))
            {
                return false;
            }

            operation = current;
            grant = currentGrant;
            maximumDeadlineUtc = DateTimeOffset.FromUnixTimeMilliseconds(
                current.LivenessDeferralDeadlineUtcMs);
            return true;
        }

        if (current.ClosureState !=
                FileTransferReceiveRecoveryClosureState.Retained ||
            current.Authority.RecoveryLivenessLease is not
                { State: RecoveryLivenessLeaseState.Active } issuedLease ||
            current.RetiredRecoveryLivenessLease is not
                { State: RecoveryLivenessLeaseState.Retired } retiredLease ||
            retiredLease != (issuedLease with
                {
                    State = RecoveryLivenessLeaseState.Retired,
                    Reason = NormalizeReason(completedReceipt.Reason),
                }) ||
            currentGrant !=
                CreateReceiveRecoveryLivenessDeferralGrant(
                    current.Authority,
                    current.LivenessDeferralDeadlineUtcMs,
                    retiredLease))
        {
            return false;
        }

        operation = current;
        grant = currentGrant;
        maximumDeadlineUtc = DateTimeOffset.FromUnixTimeMilliseconds(
            current.LivenessDeferralDeadlineUtcMs);
        return true;
    }

    internal static bool TryGetCurrentReceiveRecoveryClosure(
        FileTransferCoordinatorState state,
        out FileTransferReceiveRecoveryOperation operation)
    {
        if (state.PersistentState.FileTransferReceiveRecovery
                .CurrentOperation is { } current &&
            current.ClosureState is
                FileTransferReceiveRecoveryClosureState.Retained or
                FileTransferReceiveRecoveryClosureState.Adopted or
                FileTransferReceiveRecoveryClosureState.Reserved)
        {
            operation = current;
            return true;
        }

        operation = default;
        return false;
    }

    private static bool TryValidateReceiveRecoveryClosureRelease(
        FileTransferCoordinatorState state,
        RuntimeUnlockRetryDispatchAuthorityRequest request,
        FileTransferAuthorityId? releaseAuthorityId,
        int releaseGeneration,
        string releaseOutcome,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        var receiveRecovery =
            state.PersistentState.FileTransferReceiveRecovery;
        if (receiveRecovery.CurrentOperation is not { } operation ||
            operation.ClosureState !=
                FileTransferReceiveRecoveryClosureState.Adopted ||
            operation.RetiredRecoveryLivenessLease is not
                { } retiredLease ||
            operation.ExecutionReceipt?.Outcome !=
                FileTransferBridgeRecoveryExecutionOutcome.Completed ||
            operation.ReceiveProof is null)
        {
            rejectionReason =
                receiveRecovery.LastTerminalOperation is
                {
                    ClosureState:
                        FileTransferReceiveRecoveryClosureState.Consumed,
                    RetiredRecoveryLivenessLease: { } consumedLease,
                } &&
                consumedLease.AuthorityId == releaseAuthorityId &&
                consumedLease.LeaseGeneration == releaseGeneration
                    ? "receive_recovery_lease_stale"
                    : "receive_recovery_lease_pending";
            return false;
        }

        if (!TryValidateReceiveRecoveryObservationCurrent(
                state,
                operation.Authority.Observation,
                operation.Authority.ExecutionCommand.CheckpointRequestId,
                out rejectionReason) ||
            releaseAuthorityId != retiredLease.AuthorityId ||
            releaseGeneration != retiredLease.LeaseGeneration ||
            request.RegularV4LivenessGeneration !=
                retiredLease.LeaseGeneration ||
            request.BridgeDecisionGeneration !=
                operation.Authority.BridgeRecoveryDecision
                    .BridgeDecisionGeneration ||
            !string.Equals(
                releaseOutcome,
                nameof(RecoveryLivenessLeaseState.Retired),
                StringComparison.Ordinal))
        {
            rejectionReason = "receive_recovery_lease_stale";
            return false;
        }

        return true;
    }

    private static bool TryGetReceiveRecoveryOperationForRetryRequest(
        FileTransferCoordinatorState state,
        RuntimeUnlockRetryDispatchAuthorityRequest request,
        out FileTransferReceiveRecoveryOperation operation,
        out RecoveryLivenessLease lease)
    {
        operation = default;
        lease = default;
        if (state.PersistentState.FileTransferReceiveRecovery
                .CurrentOperation is not { } current ||
            current.Authority.RecoveryLivenessLease is not
                { } activeLease ||
            !TryValidateReceiveRecoveryObservationCurrent(
                state,
                current.Authority.Observation,
                current.Authority.ExecutionCommand.CheckpointRequestId,
                out _) ||
            !string.Equals(
                request.SessionId,
                current.Authority.Observation.SessionId,
                StringComparison.Ordinal) ||
            !string.Equals(
                request.TransferId,
                current.Authority.Observation.TransferId,
                StringComparison.Ordinal) ||
            request.Direction != current.Authority.Observation.Direction ||
            !MatchesRuntimeUnlockRetrySourcePublication(
                request.SourcePublicationAuthority,
                current.Authority.Observation.RoutePublication) ||
            request.RegularV4LivenessGeneration !=
                activeLease.LeaseGeneration ||
            request.BridgeDecisionGeneration !=
                current.Authority.BridgeRecoveryDecision
                    .BridgeDecisionGeneration)
        {
            return false;
        }

        operation = current;
        lease = current.RetiredRecoveryLivenessLease ?? activeLease;
        return true;
    }

    private static RuntimeUnlockRetryDispatchDecision?
        AuthorizeHeldReceiveRecoveryClosureRetry(
            FileTransferCoordinatorState state)
    {
        if (state.PersistentState.FileTransferReceiveRecovery
                .CurrentOperation is not
            {
                ClosureState:
                    FileTransferReceiveRecoveryClosureState.Adopted,
                HeldRetryRequest: { } heldRequest,
                RetiredRecoveryLivenessLease: { } retiredLease,
            })
        {
            return null;
        }

        return AuthorizeRuntimeUnlockRetryDispatch(
            state,
            heldRequest,
            RuntimeUnlockRetryReleaseKind.ReceiveRecoveryLeaseClosure,
            retiredLease.AuthorityId,
            retiredLease.LeaseGeneration,
            nameof(RecoveryLivenessLeaseState.Retired));
    }

    internal static FileTransferReceiveRecoveryDecision
        ObserveReceiveRecoveryExecutionStart(
            FileTransferCoordinatorState state,
            FileTransferBridgeRecoveryExecutionStartResult result,
            long observedUtcMs = 0)
    {
        FileTransferReceiveRecoveryDecision Decision(
            FileTransferReceiveRecoveryTransitionStatus status,
            string reason,
            FileTransferCoordinatorState? nextState = null,
            FileTransferReceiveRecoveryOperationAuthority? authority = null)
            => new(
                status,
                reason,
                authority ??
                state.PersistentState.FileTransferReceiveRecovery
                    .CurrentOperation?.Authority)
            {
                State = nextState ?? state,
            };

        var recoveryState = state.PersistentState.FileTransferReceiveRecovery;
        if (recoveryState.LastTerminalOperation is
            {
                ExecutionStartResult: { } terminalStart,
            } terminal &&
            terminal.Authority.ExecutionCommand == result.Command)
        {
            return IsIdempotentReceiveRecoveryExecutionStartResult(
                    terminalStart,
                    result,
                    terminal.ExecutionReceipt)
                ? Decision(
                    FileTransferReceiveRecoveryTransitionStatus.Idempotent,
                    "filetransfer_receive_recovery_execution_start_terminal_duplicate",
                    authority: terminal.Authority)
                : Decision(
                    FileTransferReceiveRecoveryTransitionStatus.Rejected,
                    "filetransfer_receive_recovery_execution_start_terminal_mismatch",
                    authority: terminal.Authority);
        }

        if (recoveryState.CurrentOperation is not { } current ||
            current.Authority.ExecutionCommand != result.Command)
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Rejected,
                "filetransfer_receive_recovery_execution_command_mismatch");
        }

        if (current.IsTerminal &&
            current.ExecutionStartResult is { } terminalCurrentStart)
        {
            return IsIdempotentReceiveRecoveryExecutionStartResult(
                    terminalCurrentStart,
                    result,
                    current.ExecutionReceipt)
                ? Decision(
                    FileTransferReceiveRecoveryTransitionStatus.Idempotent,
                    "filetransfer_receive_recovery_execution_start_terminal_duplicate")
                : Decision(
                    FileTransferReceiveRecoveryTransitionStatus.Rejected,
                    "filetransfer_receive_recovery_execution_start_terminal_mismatch");
        }

        if (current.ExecutionState ==
                FileTransferReceiveRecoveryExecutionState.Running &&
            current.ExecutionStartResult is { } retainedStart)
        {
            return IsIdempotentReceiveRecoveryExecutionStartResult(
                    retainedStart,
                    result)
                ? Decision(
                    FileTransferReceiveRecoveryTransitionStatus.Idempotent,
                    "filetransfer_receive_recovery_execution_start_duplicate")
                : Decision(
                    FileTransferReceiveRecoveryTransitionStatus.Rejected,
                    "filetransfer_receive_recovery_execution_start_mismatch");
        }

        if (current.ExecutionState !=
                FileTransferReceiveRecoveryExecutionState.Issued ||
            !result.Accepted)
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Rejected,
                "filetransfer_receive_recovery_execution_start_rejected");
        }

        if (!TryValidateReceiveRecoveryObservationCurrent(
                state,
                current.Authority.Observation,
                current.Authority.ExecutionCommand.CheckpointRequestId,
                out var currentnessReason))
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Rejected,
                currentnessReason);
        }

        var livenessDeadlineUtcMs =
            AdvanceReceiveRecoveryLivenessDeadline(
                current,
                observedUtcMs);
        var started = current with
        {
            ExecutionState =
                FileTransferReceiveRecoveryExecutionState.Running,
            ExecutionStartStatus = result.Status,
            ExecutionStartReason = NormalizeReason(result.Reason),
            ExecutionStartResult = result,
            LivenessDeferralDeadlineUtcMs =
                livenessDeadlineUtcMs,
            LivenessDeferralGrant =
                CreateReceiveRecoveryLivenessDeferralGrant(
                    current.Authority,
                    livenessDeadlineUtcMs),
        };
        var nextState = state with
        {
            PersistentState = state.PersistentState with
            {
                FileTransferReceiveRecovery = recoveryState with
                {
                    CurrentOperation = started,
                },
            },
        };
        return Decision(
            result.Status ==
                FileTransferBridgeRecoveryExecutionStartStatus.Idempotent
                ? FileTransferReceiveRecoveryTransitionStatus.Idempotent
                : FileTransferReceiveRecoveryTransitionStatus.Accepted,
            "filetransfer_receive_recovery_execution_started",
            nextState);
    }

    internal static FileTransferReceiveRecoveryDecision
        ObserveReceiveRecoveryExecutionReceipt(
            FileTransferCoordinatorState state,
            FileTransferBridgeRecoveryExecutionReceipt receipt,
            long observedUtcMs = 0)
    {
        FileTransferReceiveRecoveryDecision Decision(
            FileTransferReceiveRecoveryTransitionStatus status,
            string reason,
            FileTransferReceiveRecoveryOperationAuthority? authority = null,
            FileTransferCoordinatorState? nextState = null)
            => new(status, reason, authority)
            {
                State = nextState ?? state,
            };

        var recoveryState = state.PersistentState.FileTransferReceiveRecovery;
        if (receipt.IsDefault)
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Rejected,
                "filetransfer_receive_recovery_execution_receipt_missing");
        }

        if (recoveryState.LastTerminalOperation is { } terminal &&
            terminal.ExecutionReceipt == receipt)
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Idempotent,
                "filetransfer_receive_recovery_execution_receipt_duplicate",
                terminal.Authority);
        }

        if (recoveryState.CurrentOperation is not { } current ||
            current.Authority.ExecutionCommand != receipt.Command)
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Rejected,
                "filetransfer_receive_recovery_execution_receipt_mismatch");
        }

        if (current.ExecutionReceipt is { } priorReceipt)
        {
            return priorReceipt == receipt
                ? Decision(
                    FileTransferReceiveRecoveryTransitionStatus.Idempotent,
                    "filetransfer_receive_recovery_execution_receipt_duplicate",
                    current.Authority)
                : Decision(
                    FileTransferReceiveRecoveryTransitionStatus.Rejected,
                    "filetransfer_receive_recovery_execution_receipt_conflict",
                    current.Authority);
        }

        if (!TryValidateReceiveRecoveryObservationCurrent(
                state,
                current.Authority.Observation,
                current.Authority.ExecutionCommand.CheckpointRequestId,
                out var currentnessReason))
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Rejected,
                currentnessReason,
                current.Authority);
        }

        if (receipt.Outcome is not
                FileTransferBridgeRecoveryExecutionOutcome.Discarded &&
            current.ExecutionState !=
                FileTransferReceiveRecoveryExecutionState.Running)
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Rejected,
                "filetransfer_receive_recovery_execution_receipt_before_start",
                current.Authority);
        }

        var executionState = receipt.Outcome switch
        {
            FileTransferBridgeRecoveryExecutionOutcome.Completed =>
                FileTransferReceiveRecoveryExecutionState.Completed,
            FileTransferBridgeRecoveryExecutionOutcome.Exhausted =>
                FileTransferReceiveRecoveryExecutionState.Exhausted,
            _ => FileTransferReceiveRecoveryExecutionState.Discarded,
        };
        var activeLease =
            current.Authority.RecoveryLivenessLease;
        var regularV4Closure =
            receipt.Outcome ==
                FileTransferBridgeRecoveryExecutionOutcome.Completed &&
            activeLease is not null;
        if (regularV4Closure &&
            recoveryState.LastClosureGeneration == long.MaxValue)
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Rejected,
                "filetransfer_receive_recovery_closure_generation_exhausted",
                current.Authority);
        }

        var closureGeneration = regularV4Closure
            ? Math.Max(1, recoveryState.LastClosureGeneration + 1)
            : 0;
        var retiredLease = regularV4Closure
            ? activeLease!.Value with
            {
                State = RecoveryLivenessLeaseState.Retired,
                Reason = NormalizeReason(receipt.Reason),
            }
            : (RecoveryLivenessLease?)null;
        var livenessDeadlineUtcMs =
            receipt.Outcome ==
                FileTransferBridgeRecoveryExecutionOutcome.Completed
                ? AdvanceReceiveRecoveryLivenessDeadline(
                    current,
                    observedUtcMs)
                : current.LivenessDeferralDeadlineUtcMs;
        var completed = current with
        {
            ExecutionState = executionState,
            ExecutionStartResult =
                RetainTerminalReceiveRecoveryExecutionStartResult(
                    current.ExecutionStartResult),
            ExecutionReceipt = receipt,
            ExecutionReceiptObservedUtcMs = observedUtcMs,
            RetiredRecoveryLivenessLease = retiredLease,
            ClosureState = regularV4Closure
                ? current.ReceiveProof is null
                    ? FileTransferReceiveRecoveryClosureState.Retained
                    : FileTransferReceiveRecoveryClosureState.Adopted
                : FileTransferReceiveRecoveryClosureState.None,
            ClosureGeneration = closureGeneration,
            HeldRetryRequest = regularV4Closure
                ? current.HeldRetryRequest
                : null,
            LivenessDeferralDeadlineUtcMs = livenessDeadlineUtcMs,
            LivenessDeferralGrant =
                receipt.Outcome ==
                    FileTransferBridgeRecoveryExecutionOutcome.Completed
                    ? CreateReceiveRecoveryLivenessDeferralGrant(
                        current.Authority,
                        livenessDeadlineUtcMs,
                        retiredLease)
                    : current.LivenessDeferralGrant,
        };
        var keepCurrent =
            regularV4Closure ||
            receipt.Outcome ==
                FileTransferBridgeRecoveryExecutionOutcome.Completed &&
            current.ReceiveProof is null;
        var nextRecoveryState = recoveryState with
        {
            CurrentOperation = keepCurrent ? completed : null,
            LastTerminalOperation = keepCurrent
                ? recoveryState.LastTerminalOperation
                : completed,
            ProofStatePublication = regularV4Closure
                ? recoveryState.ProofStatePublication
                : null,
            LastClosureGeneration = Math.Max(
                recoveryState.LastClosureGeneration,
                closureGeneration),
        };
        var nextState = state with
        {
            PersistentState = state.PersistentState with
            {
                FileTransferReceiveRecovery = nextRecoveryState,
            },
        };
        nextState =
            TryIssueReceiveRecoveryProofStatePublication(
                nextState,
                out var proofStatePublicationCommand);
        var holdReconciliation =
            ReconcileRuntimeUnlockInitialActivationHold(
                nextState,
                current.Authority,
                receipt.Outcome ==
                    FileTransferBridgeRecoveryExecutionOutcome.Completed
                    ? "filetransfer_receive_recovery_execution_completed"
                    : "filetransfer_receive_recovery_execution_terminal");
        nextState = holdReconciliation.State;
        var retryDecision =
            AuthorizeHeldReceiveRecoveryClosureRetry(nextState);

        return Decision(
            FileTransferReceiveRecoveryTransitionStatus.Accepted,
            "filetransfer_receive_recovery_execution_receipt_applied",
            current.Authority,
            nextState) with
        {
            RetryDispatchDecision = retryDecision,
            InitialActivationHoldDecision =
                holdReconciliation.Decision,
            ProofStatePublicationCommand =
                proofStatePublicationCommand,
        };
    }

    internal static FileTransferReceiveRecoveryDecision
        ObserveReceiveRecoveryPeerReachability(
            FileTransferCoordinatorState state,
            FileTransferReceiveRecoveryPeerReachabilityObservation
                observation)
    {
        FileTransferReceiveRecoveryDecision Decision(
            FileTransferReceiveRecoveryTransitionStatus status,
            string reason,
            FileTransferCoordinatorState? nextState = null,
            FileTransferReceiveRecoveryProofStatePublicationCommand?
                command = null)
            => new(
                status,
                reason,
                observation.IsDefault
                    ? null
                    : state.PersistentState.FileTransferReceiveRecovery
                        .CurrentOperation?.Authority)
            {
                State = nextState ?? state,
                ProofStatePublicationCommand = command,
            };

        if (observation.IsDefault)
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Rejected,
                "filetransfer_receive_recovery_peer_reachability_observation_missing");
        }

        var recovery = state.PersistentState.FileTransferReceiveRecovery;
        if (recovery.CurrentOperation is not { } current ||
            current.Authority.ExecutionCommand !=
                observation.ExecutionCommand)
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Rejected,
                "filetransfer_receive_recovery_peer_reachability_operation_stale");
        }

        if (current.Authority.Observation.Direction !=
                FileTransferDirection.Inbound)
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Rejected,
                "filetransfer_receive_recovery_peer_reachability_direction_outbound");
        }

        if (!TryValidateReceiveRecoveryObservationCurrent(
                state,
                current.Authority.Observation,
                current.Authority.ExecutionCommand.CheckpointRequestId,
                out var currentnessReason))
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Rejected,
                currentnessReason);
        }

        if (current.ExecutionState is
                FileTransferReceiveRecoveryExecutionState.Exhausted or
                FileTransferReceiveRecoveryExecutionState.Discarded or
                FileTransferReceiveRecoveryExecutionState.Invalidated ||
            current.ReceiveProof is not null ||
            current.ClosureState is
                FileTransferReceiveRecoveryClosureState.Adopted or
                FileTransferReceiveRecoveryClosureState.Reserved or
                FileTransferReceiveRecoveryClosureState.Consumed)
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Rejected,
                "filetransfer_receive_recovery_peer_reachability_not_eligible");
        }

        var proofPublication =
            recovery.ProofStatePublication ??
            FileTransferReceiveRecoveryProofStatePublicationSubstate.Empty;
        if (proofPublication.PendingObservation is { } pending)
        {
            if (pending == observation)
            {
                return Decision(
                    FileTransferReceiveRecoveryTransitionStatus.Idempotent,
                    "filetransfer_receive_recovery_peer_reachability_duplicate",
                    state,
                    proofPublication.PendingCommand);
            }

            if (observation.ObservedUtcMs <= pending.ObservedUtcMs)
            {
                return Decision(
                    FileTransferReceiveRecoveryTransitionStatus.Rejected,
                    "filetransfer_receive_recovery_peer_reachability_not_newer");
            }
        }

        if (proofPublication.LastReceipt is { } lastReceipt)
        {
            if (lastReceipt.Command.Observation == observation)
            {
                return Decision(
                    FileTransferReceiveRecoveryTransitionStatus.Idempotent,
                    "filetransfer_receive_recovery_peer_reachability_duplicate",
                    state);
            }

            if (observation.ObservedUtcMs <=
                lastReceipt.Command.Observation.ObservedUtcMs)
            {
                return Decision(
                    FileTransferReceiveRecoveryTransitionStatus.Rejected,
                    "filetransfer_receive_recovery_peer_reachability_not_newer");
            }
        }

        if (proofPublication.LastReceipt is
            {
                Outcome:
                    FileTransferReceiveRecoveryProofStatePublicationOutcome
                        .Delivered,
            })
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Rejected,
                "filetransfer_receive_recovery_proof_state_publication_already_delivered");
        }

        var observedState = state with
        {
            PersistentState = state.PersistentState with
            {
                FileTransferReceiveRecovery = recovery with
                {
                    ProofStatePublication = proofPublication with
                    {
                        PendingObservation = observation,
                        PendingCommand = null,
                    },
                },
            },
        };
        observedState =
            TryIssueReceiveRecoveryProofStatePublication(
                observedState,
                out var issuedCommand);
        return Decision(
            FileTransferReceiveRecoveryTransitionStatus.Accepted,
            issuedCommand is null
                ? "filetransfer_receive_recovery_peer_reachability_recorded"
                : "filetransfer_receive_recovery_proof_state_publication_issued",
            observedState,
            issuedCommand);
    }

    internal static FileTransferReceiveRecoveryDecision
        ApplyReceiveRecoveryProofStatePublicationReceipt(
            FileTransferCoordinatorState state,
            FileTransferReceiveRecoveryProofStatePublicationReceipt receipt)
    {
        FileTransferReceiveRecoveryDecision Decision(
            FileTransferReceiveRecoveryTransitionStatus status,
            string reason,
            FileTransferCoordinatorState? nextState = null)
            => new(status, reason, receipt.Command.Authority)
            {
                State = nextState ?? state,
            };

        if (receipt.IsDefault)
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Rejected,
                "filetransfer_receive_recovery_proof_state_publication_receipt_missing");
        }

        var recovery = state.PersistentState.FileTransferReceiveRecovery;
        var proofPublication =
            recovery.ProofStatePublication ??
            FileTransferReceiveRecoveryProofStatePublicationSubstate.Empty;
        if (proofPublication.LastReceipt is { } prior)
        {
            if (prior == receipt)
            {
                return Decision(
                    FileTransferReceiveRecoveryTransitionStatus.Idempotent,
                    "filetransfer_receive_recovery_proof_state_publication_receipt_duplicate");
            }

            if (prior.Command == receipt.Command)
            {
                return Decision(
                    FileTransferReceiveRecoveryTransitionStatus.Rejected,
                    "filetransfer_receive_recovery_proof_state_publication_receipt_conflict");
            }
        }

        if (proofPublication.PendingCommand is not
                { } command ||
            command != receipt.Command ||
            !IsReceiveRecoveryProofStatePublicationCommandCurrent(
                state,
                command))
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Rejected,
                "filetransfer_receive_recovery_proof_state_publication_command_stale");
        }

        var nextState = state with
        {
            PersistentState = state.PersistentState with
            {
                FileTransferReceiveRecovery = recovery with
                {
                    ProofStatePublication = proofPublication with
                    {
                        PendingObservation = null,
                        PendingCommand = null,
                        LastReceipt = receipt,
                    },
                },
            },
        };
        return Decision(
            FileTransferReceiveRecoveryTransitionStatus.Accepted,
            receipt.Outcome switch
            {
                FileTransferReceiveRecoveryProofStatePublicationOutcome
                    .Delivered =>
                    "filetransfer_receive_recovery_proof_state_publication_delivered",
                FileTransferReceiveRecoveryProofStatePublicationOutcome.Held =>
                    "filetransfer_receive_recovery_proof_state_publication_held",
                _ =>
                    "filetransfer_receive_recovery_proof_state_publication_rejected",
            },
            nextState);
    }

    internal static bool
        IsReceiveRecoveryProofStatePublicationCommandCurrent(
            FileTransferCoordinatorState state,
            FileTransferReceiveRecoveryProofStatePublicationCommand command)
    {
        var recovery =
            state.PersistentState.FileTransferReceiveRecovery;
        var proofPublication =
            recovery.ProofStatePublication ??
            FileTransferReceiveRecoveryProofStatePublicationSubstate.Empty;
        if (command.IsDefault ||
            recovery.CurrentOperation is not { } current ||
            proofPublication.PendingCommand is not { } pending ||
            pending != command ||
            current.Authority != command.Authority ||
            current.ExecutionState !=
                FileTransferReceiveRecoveryExecutionState.Completed ||
            current.ExecutionReceipt is not
            {
                Outcome:
                    FileTransferBridgeRecoveryExecutionOutcome.Completed,
            } ||
            current.ClosureState !=
                FileTransferReceiveRecoveryClosureState.Retained ||
            current.ClosureGeneration != command.ClosureGeneration ||
            current.ReceiveProof is not null ||
            current.Authority.Observation.Direction !=
                FileTransferDirection.Inbound ||
            !string.Equals(
                current.Authority.Observation.RouteToken,
                FileTransferRouteResolver.RegularNknV4FastToken,
                StringComparison.Ordinal) ||
            current.Authority.Observation.ProtocolVersion !=
                FileTransferProtocol.ProtocolVersionV4 ||
            command.Observation.ExecutionCommand !=
                current.Authority.ExecutionCommand ||
            command.Observation.ObservedUtcMs <=
                current.ExecutionReceiptObservedUtcMs)
        {
            return false;
        }

        return TryValidateReceiveRecoveryObservationCurrent(
            state,
            current.Authority.Observation,
            current.Authority.ExecutionCommand.CheckpointRequestId,
            out _);
    }

    private static FileTransferCoordinatorState
        TryIssueReceiveRecoveryProofStatePublication(
            FileTransferCoordinatorState state,
            out FileTransferReceiveRecoveryProofStatePublicationCommand?
                issuedCommand)
    {
        issuedCommand = null;
        var recovery = state.PersistentState.FileTransferReceiveRecovery;
        var proofPublication =
            recovery.ProofStatePublication ??
            FileTransferReceiveRecoveryProofStatePublicationSubstate.Empty;
        if (proofPublication.PendingCommand is { } pending)
        {
            issuedCommand = pending;
            return state;
        }

        if (recovery.CurrentOperation is not { } current ||
            proofPublication.PendingObservation is not
                { } observation ||
            current.ExecutionState !=
                FileTransferReceiveRecoveryExecutionState.Completed ||
            current.ExecutionReceipt is not
            {
                Outcome:
                    FileTransferBridgeRecoveryExecutionOutcome.Completed,
            } ||
            current.ClosureState !=
                FileTransferReceiveRecoveryClosureState.Retained ||
            current.ReceiveProof is not null ||
            current.Authority.Observation.Direction !=
                FileTransferDirection.Inbound ||
            observation.ExecutionCommand !=
                current.Authority.ExecutionCommand ||
            observation.ObservedUtcMs <=
                current.ExecutionReceiptObservedUtcMs ||
            proofPublication.LastGeneration == long.MaxValue ||
            !TryValidateReceiveRecoveryObservationCurrent(
                state,
                current.Authority.Observation,
                current.Authority.ExecutionCommand.CheckpointRequestId,
                out _))
        {
            return state;
        }

        var generation = Math.Max(
            1,
            proofPublication.LastGeneration + 1);
        var command =
            new FileTransferReceiveRecoveryProofStatePublicationCommand(
                current.Authority,
                current.ClosureGeneration,
                observation,
                generation);
        issuedCommand = command;
        return state with
        {
            PersistentState = state.PersistentState with
            {
                FileTransferReceiveRecovery = recovery with
                {
                    ProofStatePublication = proofPublication with
                    {
                        PendingCommand = command,
                        LastGeneration = generation,
                    },
                },
            },
        };
    }

    internal static FileTransferReceiveRecoveryDecision
        ObserveReceiveRecoveryProof(
            FileTransferCoordinatorState state,
            FileTransferReceiveRecoveryProof proof)
    {
        FileTransferReceiveRecoveryDecision Decision(
            FileTransferReceiveRecoveryTransitionStatus status,
            string reason,
            FileTransferCoordinatorState? nextState = null)
            => new(status, reason, proof.Authority)
            {
                State = nextState ?? state,
            };

        if (proof.IsDefault)
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Rejected,
                "filetransfer_receive_recovery_proof_missing");
        }

        var recoveryState = state.PersistentState.FileTransferReceiveRecovery;
        if (recoveryState.LastTerminalOperation is
            {
                ReceiveProof: { } terminalProof,
            } terminal &&
            terminalProof == proof)
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Idempotent,
                "filetransfer_receive_recovery_proof_duplicate");
        }

        if (recoveryState.LastTerminalOperation is { } terminalOperation &&
            terminalOperation.Authority == proof.Authority)
        {
            if (terminalOperation.ExecutionState is
                FileTransferReceiveRecoveryExecutionState.Invalidated)
            {
                return Decision(
                    FileTransferReceiveRecoveryTransitionStatus.Rejected,
                    "filetransfer_receive_recovery_proof_operation_stale");
            }

            if (terminalOperation.ReceiveProof is { } retainedTerminalProof)
            {
                return retainedTerminalProof == proof
                    ? Decision(
                        FileTransferReceiveRecoveryTransitionStatus.Idempotent,
                        "filetransfer_receive_recovery_proof_duplicate")
                    : Decision(
                        FileTransferReceiveRecoveryTransitionStatus.Rejected,
                        "filetransfer_receive_recovery_proof_conflict");
            }

            var terminalCurrentnessReason = "accepted";
            if (state.IsTerminal ||
                !TryValidateReceiveRecoveryObservationCurrent(
                    state,
                    terminalOperation.Authority.Observation,
                    terminalOperation.Authority.ExecutionCommand
                        .CheckpointRequestId,
                    out terminalCurrentnessReason))
            {
                return Decision(
                    FileTransferReceiveRecoveryTransitionStatus.Rejected,
                    state.IsTerminal
                        ? "terminal_state"
                        : terminalCurrentnessReason);
            }

            var observedTerminal = terminalOperation with
            {
                ReceiveProof = proof,
                ClosureState =
                    FileTransferReceiveRecoveryClosureState.None,
            };
            var terminalState = state with
            {
                PersistentState = state.PersistentState with
                {
                    FileTransferReceiveRecovery = recoveryState with
                    {
                        LastTerminalOperation = observedTerminal,
                    },
                },
            };
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Accepted,
                "filetransfer_receive_recovery_proof_observed_after_terminal_receipt",
                terminalState);
        }

        if (recoveryState.CurrentOperation is not { } current ||
            current.Authority != proof.Authority)
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Rejected,
                "filetransfer_receive_recovery_proof_operation_stale");
        }

        if (current.ReceiveProof is { } retainedProof)
        {
            return retainedProof == proof
                ? Decision(
                    FileTransferReceiveRecoveryTransitionStatus.Idempotent,
                    "filetransfer_receive_recovery_proof_duplicate")
                : Decision(
                    FileTransferReceiveRecoveryTransitionStatus.Rejected,
                    "filetransfer_receive_recovery_proof_conflict");
        }

        var currentnessRejectionReason = "accepted";
        if (state.IsTerminal ||
            !TryValidateReceiveRecoveryObservationCurrent(
                state,
                current.Authority.Observation,
                current.Authority.ExecutionCommand.CheckpointRequestId,
                out currentnessRejectionReason))
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Rejected,
                state.IsTerminal
                    ? "terminal_state"
                    : currentnessRejectionReason);
        }

        var observed = current with
        {
            ReceiveProof = proof,
            ClosureState =
                current.ExecutionReceipt?.Outcome ==
                    FileTransferBridgeRecoveryExecutionOutcome.Completed &&
                current.ClosureState ==
                    FileTransferReceiveRecoveryClosureState.Retained
                    ? FileTransferReceiveRecoveryClosureState.Adopted
                    : current.ClosureState,
        };
        var nonRegularCompletion =
            current.ExecutionReceipt?.Outcome ==
                FileTransferBridgeRecoveryExecutionOutcome.Completed &&
            current.Authority.RecoveryLivenessLease is null;
        var nextState = state with
        {
            PersistentState = state.PersistentState with
            {
                FileTransferReceiveRecovery = recoveryState with
                {
                    CurrentOperation = nonRegularCompletion
                        ? null
                        : observed,
                    LastTerminalOperation = nonRegularCompletion
                        ? observed
                        : recoveryState.LastTerminalOperation,
                    ProofStatePublication = null,
                },
            },
        };
        var holdReconciliation =
            ReconcileRuntimeUnlockInitialActivationHold(
                nextState,
                current.Authority,
                "filetransfer_receive_recovery_proof_observed");
        nextState = holdReconciliation.State;
        var retryDecision =
            AuthorizeHeldReceiveRecoveryClosureRetry(nextState);

        return Decision(
            FileTransferReceiveRecoveryTransitionStatus.Accepted,
            "filetransfer_receive_recovery_proof_observed",
            nextState) with
        {
            RetryDispatchDecision = retryDecision,
            InitialActivationHoldDecision =
                holdReconciliation.Decision,
        };
    }

    internal static FileTransferReceiveRecoveryDecision
        ReserveReceiveRecoveryClosure(
            FileTransferCoordinatorState state,
            RuntimeUnlockRetryDispatchAuthorityRequest request,
            RuntimeUnlockRetryDispatchAuthority retryAuthority)
    {
        FileTransferReceiveRecoveryDecision Decision(
            FileTransferReceiveRecoveryTransitionStatus status,
            string reason,
            FileTransferCoordinatorState? nextState = null)
            => new(
                status,
                reason,
                state.PersistentState.FileTransferReceiveRecovery
                    .CurrentOperation?.Authority)
            {
                State = nextState ?? state,
            };

        var recoveryState = state.PersistentState.FileTransferReceiveRecovery;
        if (recoveryState.CurrentOperation is not { } current ||
            current.RetiredRecoveryLivenessLease is not { } retiredLease)
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Rejected,
                "filetransfer_receive_recovery_closure_missing");
        }

        if (!TryValidateReceiveRecoveryObservationCurrent(
                state,
                current.Authority.Observation,
                current.Authority.ExecutionCommand.CheckpointRequestId,
                out var currentnessReason))
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Rejected,
                currentnessReason);
        }

        if (current.ClosureState ==
                FileTransferReceiveRecoveryClosureState.Reserved &&
            current.ReservedRetryRequest == request &&
            current.ReservedRetryAuthority == retryAuthority)
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Idempotent,
                "filetransfer_receive_recovery_closure_reservation_duplicate");
        }

        if (current.ClosureState !=
                FileTransferReceiveRecoveryClosureState.Adopted ||
            current.ReservedRetryRequest is not null ||
            current.ReservedRetryAuthority is not null ||
            !TryValidateRuntimeUnlockRetryDispatchBinding(
                retryAuthority,
                request,
                out _) ||
            request.DispatchAttemptGeneration != 1 ||
            request.MissingDispatchSuccessorGeneration != 0 ||
            request.PredecessorAuthorityId is not null ||
            request.RequestActivePredecessorSupersession ||
            retryAuthority.ReleaseKind !=
                RuntimeUnlockRetryReleaseKind.ReceiveRecoveryLeaseClosure ||
            retryAuthority.ReleaseAuthorityId != retiredLease.AuthorityId ||
            retryAuthority.ReleaseGeneration != retiredLease.LeaseGeneration ||
            !string.Equals(
                retryAuthority.ReleaseOutcome,
                nameof(RecoveryLivenessLeaseState.Retired),
                StringComparison.Ordinal) ||
            retryAuthority.RuntimeOwner !=
                current.Authority.Observation.RuntimeOwner ||
            retryAuthority.SourcePublicationAuthority !=
                current.Authority.Observation.RoutePublication ||
            retryAuthority.Route !=
                current.Authority.Observation.Route ||
            !string.Equals(
                retryAuthority.RouteToken,
                current.Authority.Observation.RouteToken,
                StringComparison.Ordinal) ||
            retryAuthority.ProtocolVersion !=
                current.Authority.Observation.ProtocolVersion ||
            retryAuthority.LiveRouteEpoch !=
                current.Authority.Observation.LiveRouteEpoch ||
            retryAuthority.TerminalGeneration !=
                current.Authority.Observation.TerminalGeneration ||
            retryAuthority.LegGeneration !=
                current.Authority.Observation.LegGeneration ||
            retryAuthority.TransportEpoch !=
                current.Authority.Observation.TransportEpoch ||
            retryAuthority.BridgeRecoveryGeneration !=
                current.Authority.Observation.BridgeRecoveryGeneration ||
            request.RegularV4LivenessGeneration !=
                retiredLease.LeaseGeneration ||
            request.BridgeDecisionGeneration !=
                current.Authority.BridgeRecoveryDecision
                    .BridgeDecisionGeneration ||
            retryAuthority.ReceiveRecoveryOperationGeneration !=
                current.Authority.OperationGeneration ||
            retryAuthority.ReceiveRecoveryDataSessionGeneration !=
                current.Authority.Observation.DataSessionGeneration ||
            retryAuthority.SessionId != request.SessionId ||
            retryAuthority.TransferId != request.TransferId ||
            retryAuthority.Direction != request.Direction)
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Rejected,
                "filetransfer_receive_recovery_closure_reservation_mismatch");
        }

        var reserved = current with
        {
            ClosureState =
                FileTransferReceiveRecoveryClosureState.Reserved,
            HeldRetryRequest = null,
            ReservedRetryRequest = request,
            ReservedRetryAuthority = retryAuthority,
        };
        var nextState = state with
        {
            PersistentState = state.PersistentState with
            {
                FileTransferReceiveRecovery = recoveryState with
                {
                    CurrentOperation = reserved,
                },
            },
        };
        return Decision(
            FileTransferReceiveRecoveryTransitionStatus.Accepted,
            "filetransfer_receive_recovery_closure_reserved",
            nextState);
    }

    internal static FileTransferReceiveRecoveryDecision
        ConsumeReceiveRecoveryClosure(
            FileTransferCoordinatorState state,
            RuntimeUnlockRetryDispatchInstallReceipt receipt)
    {
        FileTransferReceiveRecoveryDecision Decision(
            FileTransferReceiveRecoveryTransitionStatus status,
            string reason,
            FileTransferReceiveRecoveryOperationAuthority? authority = null,
            FileTransferCoordinatorState? nextState = null)
            => new(status, reason, authority)
            {
                State = nextState ?? state,
            };

        var recoveryState = state.PersistentState.FileTransferReceiveRecovery;
        if (recoveryState.LastTerminalOperation is
            {
                ClosureState:
                    FileTransferReceiveRecoveryClosureState.Consumed,
                ConsumingInstallReceipt: { } consumedReceipt,
            } consumed &&
            consumedReceipt == receipt)
        {
            if (!TryValidateReceiveRecoveryObservationCurrent(
                    state,
                    consumed.Authority.Observation,
                    consumed.Authority.ExecutionCommand.CheckpointRequestId,
                    out var duplicateCurrentnessReason))
            {
                return Decision(
                    FileTransferReceiveRecoveryTransitionStatus.Rejected,
                    duplicateCurrentnessReason,
                    consumed.Authority);
            }

            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Idempotent,
                "filetransfer_receive_recovery_closure_consumption_duplicate",
                consumed.Authority);
        }

        var retryLifecycle = GetRuntimeUnlockRetryLifecycle(state);
        if (recoveryState.CurrentOperation is not { } current)
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Rejected,
                "filetransfer_receive_recovery_closure_consumption_mismatch");
        }

        if (!TryValidateReceiveRecoveryObservationCurrent(
                state,
                current.Authority.Observation,
                current.Authority.ExecutionCommand.CheckpointRequestId,
                out var currentnessReason))
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Rejected,
                currentnessReason,
                current.Authority);
        }

        if (current.ClosureState !=
                FileTransferReceiveRecoveryClosureState.Reserved ||
            current.ReservedRetryRequest != receipt.Request ||
            current.ReservedRetryAuthority != receipt.Authority ||
            receipt.IsDefault ||
            retryLifecycle.InstalledAttempt is not { IsInstalled: true }
                installedAttempt ||
            installedAttempt.Request != receipt.Request ||
            installedAttempt.Authority != receipt.Authority ||
            installedAttempt.InstallReceipt != receipt)
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Rejected,
                "filetransfer_receive_recovery_closure_consumption_mismatch");
        }

        var consumedOperation = current with
        {
            ClosureState =
                FileTransferReceiveRecoveryClosureState.Consumed,
            ReservedRetryRequest = receipt.Request,
            ConsumingInstallReceipt = receipt,
        };
        var nextState = state with
        {
            PersistentState = state.PersistentState with
            {
                FileTransferReceiveRecovery = recoveryState with
                {
                    CurrentOperation = null,
                    LastTerminalOperation = consumedOperation,
                },
            },
        };
        return Decision(
            FileTransferReceiveRecoveryTransitionStatus.Accepted,
            "filetransfer_receive_recovery_closure_consumed",
            current.Authority,
            nextState);
    }

    private static FileTransferCoordinatorState
        InvalidateRuntimeUnlockRetryReleaseReservationsForRetryAttempts(
            FileTransferCoordinatorState state,
            RuntimeUnlockRetryLifecycleAttemptSnapshot? primaryAttempt,
            RuntimeUnlockRetryLifecycleAttemptSnapshot? secondaryAttempt,
            string reason)
    {
        var nextState = state;
        var operation = state.PersistentState.FileTransferReceiveRecovery
            .CurrentOperation;
        if (operation is
            {
                ClosureState:
                    FileTransferReceiveRecoveryClosureState.Reserved,
                ReservedRetryRequest: { } reservedRequest,
                ReservedRetryAuthority: { } reservedAuthority,
            } current &&
            (RuntimeUnlockRetryAttemptMatchesReceiveRecoveryReservation(
                 primaryAttempt,
                 reservedRequest,
                 reservedAuthority) ||
             RuntimeUnlockRetryAttemptMatchesReceiveRecoveryReservation(
                 secondaryAttempt,
                 reservedRequest,
                 reservedAuthority)))
        {
            nextState = InvalidateReceiveRecoveryOperation(
                state,
                current.Authority,
                reason).State;
        }

        return ClearFallbackTailPairedReleaseReservationForRetryAttempts(
            nextState,
            primaryAttempt,
            secondaryAttempt);
    }

    private static bool RuntimeUnlockRetryAttemptMatchesReceiveRecoveryReservation(
        RuntimeUnlockRetryLifecycleAttemptSnapshot? attempt,
        RuntimeUnlockRetryDispatchAuthorityRequest reservedRequest,
        RuntimeUnlockRetryDispatchAuthority reservedAuthority)
        => attempt is { } exact &&
           exact.Request == reservedRequest &&
           exact.Authority == reservedAuthority;

    private static FileTransferCoordinatorState
        ClearFallbackTailPairedReleaseReservationForRetryAttempts(
            FileTransferCoordinatorState state,
            RuntimeUnlockRetryLifecycleAttemptSnapshot? primaryAttempt,
            RuntimeUnlockRetryLifecycleAttemptSnapshot? secondaryAttempt)
    {
        var fallback = state.PersistentState.FallbackTailYield;
        if (fallback.CurrentReservation is not { } reservation ||
            !RuntimeUnlockRetryAttemptMatchesFallbackTailReservation(
                primaryAttempt,
                reservation) &&
            !RuntimeUnlockRetryAttemptMatchesFallbackTailReservation(
                secondaryAttempt,
                reservation) ||
            fallback.CurrentOperation is not
            {
                PairedRelease: { } currentRelease,
            } operation ||
            currentRelease != reservation.Release)
        {
            return state;
        }

        var releasedState = ApplyFallbackTailYieldSubstate(
            state,
            fallback with
            {
                CurrentOperation = operation with
                {
                    PairingState =
                        FileTransferFallbackTailYieldPairingState.Paired,
                },
                CurrentReservation = null,
            });
        return AttachFallbackTailPairedReleaseRetryRecheckRequest(
            releasedState);
    }

    private static bool RuntimeUnlockRetryAttemptMatchesFallbackTailReservation(
        RuntimeUnlockRetryLifecycleAttemptSnapshot? attempt,
        FileTransferFallbackTailPairedReleaseReservation reservation)
        => attempt is { } exact &&
           exact.Authority == reservation.RetryAuthority &&
           TryValidateRuntimeUnlockRetryDispatchBinding(
               exact.Authority,
               exact.Request,
               out _);

    internal static FileTransferReceiveRecoveryDecision
        InvalidateReceiveRecoveryOperation(
            FileTransferCoordinatorState state,
            FileTransferReceiveRecoveryOperationAuthority authority,
            string reason)
    {
        FileTransferReceiveRecoveryDecision Decision(
            FileTransferReceiveRecoveryTransitionStatus status,
            string decisionReason,
            FileTransferCoordinatorState? nextState = null)
            => new(status, decisionReason, authority)
            {
                State = nextState ?? state,
            };

        var recoveryState = state.PersistentState.FileTransferReceiveRecovery;
        if (recoveryState.LastTerminalOperation is
            {
                ExecutionState:
                    FileTransferReceiveRecoveryExecutionState.Invalidated,
            } terminal &&
            terminal.Authority == authority)
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Idempotent,
                "filetransfer_receive_recovery_invalidation_duplicate");
        }

        if (recoveryState.CurrentOperation is not { } current ||
            current.Authority != authority)
        {
            return Decision(
                FileTransferReceiveRecoveryTransitionStatus.Rejected,
                "filetransfer_receive_recovery_invalidation_stale");
        }

        var nextState = state;
        if (current.ClosureState ==
                FileTransferReceiveRecoveryClosureState.Reserved &&
            current.ReservedRetryRequest is { } reservedRequest &&
            current.ReservedRetryAuthority is { } reservedAuthority)
        {
            var retryLifecycle = GetRuntimeUnlockRetryLifecycle(nextState);
            if (retryLifecycle.PendingAttempt is { } pendingAttempt &&
                pendingAttempt.Request == reservedRequest &&
                pendingAttempt.Authority == reservedAuthority)
            {
                var invalidatedAttempt = pendingAttempt with
                {
                    State =
                        RuntimeUnlockRetryLifecycleAttemptState.Invalidated,
                };
                nextState = ApplyRuntimeUnlockRetryLifecycle(
                    nextState,
                    retryLifecycle with
                    {
                        PendingAttempt = null,
                        LastTerminalAttempt = invalidatedAttempt,
                        AttemptHistory =
                            AppendRuntimeUnlockRetryLifecycleAttemptHistory(
                                retryLifecycle.AttemptHistory,
                                invalidatedAttempt),
                    });
            }
        }

        var invalidated = current with
        {
            ExecutionState =
                FileTransferReceiveRecoveryExecutionState.Invalidated,
            ClosureState = FileTransferReceiveRecoveryClosureState.None,
            HeldRetryRequest = null,
            ReservedRetryRequest = null,
            ReservedRetryAuthority = null,
            InvalidationReason = NormalizeReason(reason),
        };
        nextState = nextState with
        {
            PersistentState = nextState.PersistentState with
            {
                FileTransferReceiveRecovery = recoveryState with
                {
                    CurrentOperation = null,
                    LastTerminalOperation = invalidated,
                    ProofStatePublication = null,
                },
            },
        };
        var holdReconciliation =
            ReconcileRuntimeUnlockInitialActivationHold(
                nextState,
                authority,
                "filetransfer_receive_recovery_operation_invalidated");
        return Decision(
            FileTransferReceiveRecoveryTransitionStatus.Accepted,
            "filetransfer_receive_recovery_operation_invalidated",
            holdReconciliation.State) with
        {
            InitialActivationHoldDecision =
                holdReconciliation.Decision,
        };
    }

    private static bool TryValidateReceiveRecoveryObservationCurrent(
        FileTransferCoordinatorState state,
        FileTransferReceiveRecoveryObservation observation,
        string? checkpointRequestId,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        if (observation.IsDefault)
        {
            rejectionReason =
                "filetransfer_receive_recovery_observation_missing";
            return false;
        }

        if (state.IsTerminal)
        {
            rejectionReason = "terminal_state";
            return false;
        }

        if (state.CurrentRuntimeOwner is not { } owner ||
            state.CurrentLeg is not { } leg ||
            state.CurrentRoutePublicationToken is not { } publication ||
            string.IsNullOrWhiteSpace(state.CurrentDataSessionIdentity) ||
            state.CurrentDataSessionGeneration <= 0)
        {
            rejectionReason =
                "filetransfer_receive_recovery_current_scope_missing";
            return false;
        }

        if (!string.Equals(
                observation.Source,
                NormalizeReason(observation.Source),
                StringComparison.Ordinal) ||
            !string.Equals(
                observation.Reason,
                NormalizeReason(observation.Reason),
                StringComparison.Ordinal) ||
            !string.Equals(
                observation.ObservationIdentity,
                NormalizeReason(observation.ObservationIdentity),
                StringComparison.Ordinal))
        {
            rejectionReason =
                "filetransfer_receive_recovery_observation_reason_invalid";
            return false;
        }

        if (!string.Equals(
                observation.SessionId,
                state.SessionId,
                StringComparison.Ordinal) ||
            !string.Equals(
                observation.TransferId,
                state.TransferId,
                StringComparison.Ordinal) ||
            observation.Direction != state.Direction ||
            observation.Route != state.RouteSelection.Route ||
            !string.Equals(
                observation.RouteToken,
                state.RouteSelection.TelemetryToken,
                StringComparison.Ordinal) ||
            observation.ProtocolVersion !=
                state.RouteSelection.ProtocolVersion ||
            observation.LiveRouteEpoch !=
                (state.CurrentLiveRouteEpoch?.EpochId ?? 0) ||
            observation.RuntimeOwner != owner ||
            observation.RoutePublication != publication ||
            observation.RouteCommit != state.CurrentRouteCommit ||
            observation.TerminalGeneration != state.TerminalGeneration ||
            observation.LegGeneration != leg.Generation ||
            observation.TransportEpoch != leg.TransportEpochId ||
            observation.BridgeRecoveryGeneration !=
                leg.BridgeRecoveryGeneration ||
            !string.Equals(
                observation.CheckpointRequestId,
                checkpointRequestId,
                StringComparison.Ordinal) ||
            !string.Equals(
                observation.CheckpointRequestId,
                leg.CheckpointRequestId,
                StringComparison.Ordinal))
        {
            rejectionReason =
                "filetransfer_receive_recovery_observation_scope_stale";
            return false;
        }

        if (!string.Equals(
                observation.DataSessionIdentity,
                state.CurrentDataSessionIdentity,
                StringComparison.Ordinal) ||
            observation.DataSessionGeneration !=
                state.CurrentDataSessionGeneration)
        {
            rejectionReason =
                "filetransfer_receive_recovery_observation_data_session_stale";
            return false;
        }

        if (!IsRoutePublicationTokenCurrent(
                state,
                publication,
                out rejectionReason))
        {
            return false;
        }

        if (observation.RouteCommit is { } routeCommit &&
            !IsRouteCommitTokenCurrent(state, routeCommit, out rejectionReason))
        {
            return false;
        }

        return true;
    }

    private static bool IsIdempotentReceiveRecoveryExecutionStartResult(
        FileTransferBridgeRecoveryExecutionStartResult retained,
        FileTransferBridgeRecoveryExecutionStartResult candidate,
        FileTransferBridgeRecoveryExecutionReceipt? terminalReceipt = null)
        => IsRetainedReceiveRecoveryExecutionStartResultValid(
               retained,
               terminalReceipt is not null) &&
           candidate.Accepted &&
           retained.Command == candidate.Command &&
           (ReferenceEquals(retained.Completion, candidate.Completion) ||
             terminalReceipt is { } receipt &&
            candidate.Status ==
                FileTransferBridgeRecoveryExecutionStartStatus.Idempotent &&
            candidate.Completion is
            {
                IsCompletedSuccessfully: true,
             } completion &&
             completion.Result == receipt);

    private static bool IsRetainedReceiveRecoveryExecutionStartResultValid(
        FileTransferBridgeRecoveryExecutionStartResult retained,
        bool terminal)
        => terminal
            ? !retained.Command.IsDefault &&
              retained.Status is
                  FileTransferBridgeRecoveryExecutionStartStatus.Started or
                  FileTransferBridgeRecoveryExecutionStartStatus.Idempotent &&
              !string.IsNullOrWhiteSpace(retained.Reason) &&
              string.Equals(
                  retained.Reason,
                  retained.Reason.Trim(),
                  StringComparison.Ordinal) &&
              retained.Completion is null
            : retained.Accepted;

    private static FileTransferBridgeRecoveryExecutionStartResult?
        RetainTerminalReceiveRecoveryExecutionStartResult(
            FileTransferBridgeRecoveryExecutionStartResult? result)
        => result is { } exact
            ? exact with { Completion = null }
            : null;

    private static bool RequiresRegularV4ReceiveRecoveryLease(
        FileTransferCoordinatorState state)
        => state.RouteSelection.Route ==
               FileTransferRoute.RegularNknV4Fast &&
           state.RouteSelection.ProtocolVersion ==
               FileTransferProtocol.ProtocolVersionV4;

    private static FileTransferAuthorityId
        IssueReceiveRecoveryOperationAuthorityId(
            FileTransferCoordinatorState state,
            long operationGeneration)
    {
        var canonical = string.Join(
            "|",
            FileTransferAuthoritySchema.Current.ToString(
                CultureInfo.InvariantCulture),
            "filetransfer_receive_recovery_operation",
            state.SessionId,
            state.TransferId,
            state.Direction.ToString(),
            state.CurrentRuntimeOwner?.RuntimeGeneration.ToString(
                CultureInfo.InvariantCulture) ?? "0",
            state.TerminalGeneration.ToString(
                CultureInfo.InvariantCulture),
            state.CurrentLeg?.Generation.ToString(
                CultureInfo.InvariantCulture) ?? "0",
            operationGeneration.ToString(CultureInfo.InvariantCulture));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return new FileTransferAuthorityId(
            $"fta{FileTransferAuthoritySchema.Current}-filetransfer_receive_recovery_operation-{Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant()}");
    }

    internal static FileTransferBridgeRecoveryExecutionDecision
        ObserveBridgeRecoveryExecutionStart(
            FileTransferCoordinatorState state,
            FileTransferBridgeRecoveryExecutionStartResult result)
    {
        var transition =
            ObserveReceiveRecoveryExecutionStart(state, result);
        return new FileTransferBridgeRecoveryExecutionDecision(
            transition.Status switch
            {
                FileTransferReceiveRecoveryTransitionStatus.Accepted =>
                    FileTransferBridgeRecoveryExecutionDecisionStatus.Accepted,
                FileTransferReceiveRecoveryTransitionStatus.Idempotent =>
                    FileTransferBridgeRecoveryExecutionDecisionStatus.Idempotent,
                _ =>
                    FileTransferBridgeRecoveryExecutionDecisionStatus.Rejected,
            },
            transition.Reason,
            result.Command)
        {
            State = transition.State,
        };
    }

    internal static FileTransferBridgeRecoveryExecutionDecision
        ObserveBridgeRecoveryExecutionReceipt(
            FileTransferCoordinatorState state,
            FileTransferBridgeRecoveryExecutionReceipt receipt)
    {
        var transition =
            ObserveReceiveRecoveryExecutionReceipt(state, receipt);
        return new FileTransferBridgeRecoveryExecutionDecision(
            transition.Status switch
            {
                FileTransferReceiveRecoveryTransitionStatus.Accepted =>
                    FileTransferBridgeRecoveryExecutionDecisionStatus.Accepted,
                FileTransferReceiveRecoveryTransitionStatus.Idempotent =>
                    FileTransferBridgeRecoveryExecutionDecisionStatus.Idempotent,
                _ =>
                    FileTransferBridgeRecoveryExecutionDecisionStatus.Rejected,
            },
            transition.Reason,
            receipt.Command)
        {
            State = transition.State,
        };
    }

    internal static bool IsBridgeRecoveryExecutionCommandCurrent(
        FileTransferCoordinatorState state,
        FileTransferBridgeRecoveryExecutionCommand command)
    {
        if (command.IsDefault ||
            state.IsTerminal ||
            state.PersistentState.FileTransferReceiveRecovery
                .CurrentOperation is not { } operation ||
            operation.Authority.ExecutionCommand != command ||
            operation.ExecutionState is not (
                FileTransferReceiveRecoveryExecutionState.Issued or
                FileTransferReceiveRecoveryExecutionState.Running) ||
            state.CurrentRuntimeOwner is not { } owner ||
            state.CurrentLeg is not { } leg ||
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

        if (!TryValidateReceiveRecoveryObservationCurrent(
                state,
                operation.Authority.Observation,
                command.CheckpointRequestId,
                out _))
        {
            return false;
        }

        var requiresRecoveryLease =
            state.RouteSelection.Route == FileTransferRoute.RegularNknV4Fast &&
            state.RouteSelection.ProtocolVersion == FileTransferProtocol.ProtocolVersionV4;
        return requiresRecoveryLease
            ? command.RecoveryLivenessLease is { } commandLease &&
              operation.Authority.RecoveryLivenessLease is { } currentLease &&
              commandLease == currentLease &&
              RuntimeUnlockRegularV4RecoveryLeaseMatchesCurrentRuntime(state, commandLease) &&
              commandLease.State == RecoveryLivenessLeaseState.Active
            : command.RecoveryLivenessLease is null;
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

        if (GetRuntimeUnlockRetryLifecycle(state).CurrentAuthorizedAuthority is not
                { } currentAuthority ||
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

        if (authority.ReleaseKind ==
                RuntimeUnlockRetryReleaseKind.ReceiveRecoveryLeaseClosure &&
            (!string.Equals(
                 authority.ReleaseOutcome,
                 nameof(RecoveryLivenessLeaseState.Retired),
                 StringComparison.Ordinal) ||
             authority.ReceiveRecoveryOperationGeneration <= 0 ||
             authority.ReceiveRecoveryDataSessionGeneration <= 0))
        {
            return RuntimeUnlockRetryDispatchMismatch(
                "receive_recovery_operation",
                out mismatchField);
        }

        if (authority.ReleaseKind ==
                RuntimeUnlockRetryReleaseKind.ReceiveRecoveryLeaseClosure)
        {
            var recoveryState =
                state.PersistentState.FileTransferReceiveRecovery;
            var operation =
                recoveryState.CurrentOperation ??
                recoveryState.LastTerminalOperation;
            if (operation is not { } recoveryOperation ||
                recoveryOperation.Authority.OperationGeneration !=
                    authority.ReceiveRecoveryOperationGeneration ||
                recoveryOperation.Authority.Observation
                        .DataSessionGeneration !=
                    authority.ReceiveRecoveryDataSessionGeneration ||
                recoveryOperation.RetiredRecoveryLivenessLease is not
                    { } retiredLease ||
                retiredLease.AuthorityId != authority.ReleaseAuthorityId ||
                retiredLease.LeaseGeneration !=
                    authority.ReleaseGeneration)
            {
                return RuntimeUnlockRetryDispatchMismatch(
                    "receive_recovery_operation",
                    out mismatchField);
            }
        }

        if (authority.ReleaseKind !=
                RuntimeUnlockRetryReleaseKind.ReceiveRecoveryLeaseClosure &&
            (authority.ReceiveRecoveryOperationGeneration != 0 ||
             authority.ReceiveRecoveryDataSessionGeneration != 0))
        {
            return RuntimeUnlockRetryDispatchMismatch(
                "receive_recovery_operation",
                out mismatchField);
        }

        return true;
    }

    internal static bool TryIssueRuntimeUnlockRetryDispatchInstallReceipt(
        FileTransferCoordinatorState state,
        RuntimeUnlockRetryDispatchDecision stagedDecision,
        RuntimeUnlockRetryDispatchAuthorityRequest expectedRequest,
        out RuntimeUnlockRetryDispatchInstallReceipt receipt,
        out string mismatchField)
    {
        receipt = default;
        mismatchField = "none";
        if (state.IsTerminal)
        {
            return RuntimeUnlockRetryDispatchMismatch(
                "terminal_state",
                out mismatchField);
        }

        var lifecycle = GetRuntimeUnlockRetryLifecycle(state);
        if (lifecycle.InstalledAttempt is { } installed &&
            installed.Authority == stagedDecision.Contract?.Authority &&
            installed.InstallReceipt is { IsDefault: false } installedReceipt)
        {
            if (installed.Request != expectedRequest ||
                stagedDecision.Request != expectedRequest)
            {
                return RuntimeUnlockRetryDispatchMismatch(
                    "current_install_receipt",
                    out mismatchField);
            }

            receipt = installedReceipt;
            return true;
        }

        if (lifecycle.PendingAttempt is
            {
                State: RuntimeUnlockRetryLifecycleAttemptState.Staged,
                InstallReceipt: { IsDefault: false } issuedReceipt,
            } issued &&
            issued.Authority == stagedDecision.Contract?.Authority)
        {
            if (issued.Request != expectedRequest ||
                stagedDecision.Request != expectedRequest ||
                issuedReceipt.Authority != issued.Authority ||
                issuedReceipt.Request != expectedRequest ||
                issuedReceipt.InstallGeneration !=
                    lifecycle.LastInstallGeneration)
            {
                return RuntimeUnlockRetryDispatchMismatch(
                    "issued_install_receipt",
                    out mismatchField);
            }

            receipt = issuedReceipt;
            return true;
        }

        if (!stagedDecision.Granted ||
            stagedDecision.Contract is not { } stagedContract ||
            lifecycle.PendingAttempt is not
            {
                State: RuntimeUnlockRetryLifecycleAttemptState.Staged,
            } pending ||
            pending.Authority != stagedContract.Authority ||
            pending.Request != expectedRequest ||
            pending.StageStatus is not
                (RuntimeUnlockRetryDispatchDecisionStageStatus.Staged or
                 RuntimeUnlockRetryDispatchDecisionStageStatus.Idempotent) ||
            pending.StageDisposition !=
                RuntimeUnlockRetryDispatchStageDisposition.Retained ||
            pending.StagedUtcMs <= 0 ||
            pending.StageExpiresUtcMs <= pending.StagedUtcMs)
        {
            return RuntimeUnlockRetryDispatchMismatch(
                "coordinator_stage",
                out mismatchField);
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

        var authority = pending.Authority;
        if (lifecycle.LastAuthorityGeneration != authority.AuthorityGeneration)
        {
            return RuntimeUnlockRetryDispatchMismatch(
                "authority_generation",
                out mismatchField);
        }

        if (authority.PredecessorAuthorityId is
            { IsDefault: false } predecessorAuthorityId)
        {
            if (lifecycle.InstalledAttempt is not { } predecessor ||
                predecessor.Authority.AuthorityId != predecessorAuthorityId)
            {
                return RuntimeUnlockRetryDispatchMismatch(
                    "predecessor_lineage",
                    out mismatchField);
            }
            if (pending.Request.RequestActivePredecessorSupersession)
            {
                if (predecessor.IsTerminal ||
                    predecessor.State ==
                        RuntimeUnlockRetryLifecycleAttemptState.SendInFlight ||
                    predecessor.Authority.DispatchAttemptGeneration + 1 !=
                        authority.DispatchAttemptGeneration ||
                    predecessor.Authority.MissingDispatchSuccessorGeneration !=
                        authority.MissingDispatchSuccessorGeneration)
                {
                    return RuntimeUnlockRetryDispatchMismatch(
                        "active_predecessor_lineage",
                        out mismatchField);
                }
            }
            else if (predecessor.Settlement is not
                     {
                         Outcome:
                             RuntimeUnlockRetryDispatchSettlementOutcome
                                 .MissingDispatch,
                     } predecessorSettlement ||
                     predecessorSettlement.Authority.AuthorityId !=
                         predecessorAuthorityId)
            {
                return RuntimeUnlockRetryDispatchMismatch(
                    "missing_dispatch_predecessor_lineage",
                    out mismatchField);
            }
        }
        else if (pending.Request.PredecessorAuthorityId is not null ||
                 pending.Request.RequestActivePredecessorSupersession ||
                 lifecycle.InstalledAttempt is not null)
        {
            return RuntimeUnlockRetryDispatchMismatch(
                "initial_predecessor_lineage",
                out mismatchField);
        }

        if (lifecycle.LastInstallGeneration == long.MaxValue)
        {
            return RuntimeUnlockRetryDispatchMismatch(
                "install_generation",
                out mismatchField);
        }

        receipt = new RuntimeUnlockRetryDispatchInstallReceipt(
            authority,
            Math.Max(1, lifecycle.LastInstallGeneration + 1))
        {
            Request = expectedRequest,
            StageStatus = pending.StageStatus.Value,
            StageDisposition = pending.StageDisposition,
            StageCleared = false,
            StagedUtcMs = pending.StagedUtcMs,
            StageExpiresUtcMs = pending.StageExpiresUtcMs,
            DispatchDeadlineUtcMs =
                pending.StagedUtcMs +
                GetRuntimeUnlockRetryDispatchDeadlineMilliseconds(),
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

        if (authority.ReleaseKind ==
                RuntimeUnlockRetryReleaseKind.ReceiveRecoveryLeaseClosure
            ? authority.ReceiveRecoveryOperationGeneration <= 0 ||
              authority.ReceiveRecoveryDataSessionGeneration <= 0
            : authority.ReceiveRecoveryOperationGeneration != 0 ||
              authority.ReceiveRecoveryDataSessionGeneration != 0)
        {
            return RuntimeUnlockRetryDispatchMismatch(
                "receive_recovery_operation",
                out mismatchField);
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

        if (authority.ListenerRearmRequirement !=
            request.ListenerRearmRequirement)
        {
            return RuntimeUnlockRetryDispatchMismatch(
                "request_listener_rearm_requirement",
                out mismatchField);
        }

        return true;
    }

    private static bool RuntimeUnlockRetryDispatchAuthorityMatchesCurrentScope(
        FileTransferCoordinatorState state,
        RuntimeUnlockRetryDispatchAuthority authority,
        out string rejectionReason,
        bool allowTerminalState = false)
    {
        rejectionReason = "accepted";
        if ((state.IsTerminal && !allowTerminalState) ||
            state.CurrentRuntimeOwner is not { } owner ||
            state.CurrentRoutePublicationToken is not { } publication ||
            state.CurrentLeg is not { } leg)
        {
            rejectionReason = state.IsTerminal && !allowTerminalState
                ? "terminal_state"
                : "retry_dispatch_runtime_scope_missing";
            return false;
        }

        if (!string.Equals(authority.SessionId, state.SessionId, StringComparison.Ordinal) ||
            !string.Equals(authority.TransferId, state.TransferId, StringComparison.Ordinal) ||
            authority.Direction != state.Direction ||
            authority.RuntimeOwner != owner ||
            authority.SourcePublicationAuthority != publication ||
            authority.Route != state.RouteSelection.Route ||
            !string.Equals(
                authority.RouteToken,
                state.RouteSelection.TelemetryToken,
                StringComparison.Ordinal) ||
            authority.ProtocolVersion != state.RouteSelection.ProtocolVersion ||
            authority.LiveRouteEpoch != (state.CurrentLiveRouteEpoch?.EpochId ?? 0) ||
            authority.RuntimeGeneration != owner.RuntimeGeneration ||
            authority.TerminalGeneration != state.TerminalGeneration ||
            authority.LegGeneration != leg.Generation ||
            authority.TransportEpoch != leg.TransportEpochId ||
            authority.BridgeRecoveryGeneration != leg.BridgeRecoveryGeneration ||
            authority.RoutePublicationGeneration != publication.PublicationGeneration ||
            authority.RouteCommitGeneration != publication.RouteCommitGeneration)
        {
            rejectionReason = "retry_dispatch_runtime_scope_mismatch";
            return false;
        }

        return true;
    }

    private static FileTransferCoordinatorState ApplyRuntimeUnlockRetryLifecycle(
        FileTransferCoordinatorState state,
        RuntimeUnlockRetryLifecycleSubstate lifecycle)
    {
        var persistent = state.PersistentState with
        {
            RuntimeUnlockRetryLifecycle = lifecycle,
        };
        return state with
        {
            PersistentState = persistent,
        };
    }

    private static ImmutableArray<RuntimeUnlockRetryLifecycleAttemptSnapshot>
        AppendRuntimeUnlockRetryLifecycleAttemptHistory(
            ImmutableArray<RuntimeUnlockRetryLifecycleAttemptSnapshot> history,
            RuntimeUnlockRetryLifecycleAttemptSnapshot attempt)
    {
        var current = history.IsDefault
            ? ImmutableArray<RuntimeUnlockRetryLifecycleAttemptSnapshot>.Empty
            : history;
        for (var index = 0; index < current.Length; index++)
        {
            if (current[index].Authority.AuthorityId !=
                attempt.Authority.AuthorityId)
            {
                continue;
            }

            return current[index] == attempt
                ? current
                : current.SetItem(index, attempt);
        }

        return current.Add(attempt);
    }

    private static bool RuntimeUnlockRetryLifecycleContainsTerminalBoundary(
        RuntimeUnlockRetryLifecycleSubstate lifecycle,
        RuntimeUnlockRetryDispatchAuthorityRequest request,
        RuntimeUnlockRetryReleaseKind releaseKind,
        FileTransferAuthorityId? releaseAuthorityId,
        int releaseGeneration,
        string releaseOutcome)
    {
        static bool Matches(
            RuntimeUnlockRetryLifecycleAttemptSnapshot attempt,
            RuntimeUnlockRetryDispatchAuthorityRequest expectedRequest,
            RuntimeUnlockRetryReleaseKind expectedReleaseKind,
            FileTransferAuthorityId? expectedReleaseAuthorityId,
            int expectedReleaseGeneration,
            string expectedReleaseOutcome)
            => attempt.IsTerminal &&
               attempt.Request == expectedRequest &&
               (expectedReleaseKind ==
                    RuntimeUnlockRetryReleaseKind.Unknown ||
                attempt.Authority.ReleaseKind == expectedReleaseKind &&
                attempt.Authority.ReleaseAuthorityId ==
                    expectedReleaseAuthorityId &&
                attempt.Authority.ReleaseGeneration ==
                    expectedReleaseGeneration &&
                string.Equals(
                    attempt.Authority.ReleaseOutcome,
                    expectedReleaseOutcome,
                    StringComparison.Ordinal));

        var history = lifecycle.AttemptHistory.IsDefault
            ? ImmutableArray<RuntimeUnlockRetryLifecycleAttemptSnapshot>.Empty
            : lifecycle.AttemptHistory;
        foreach (var attempt in history)
        {
            if (Matches(
                    attempt,
                    request,
                    releaseKind,
                    releaseAuthorityId,
                    releaseGeneration,
                    releaseOutcome))
            {
                return true;
            }
        }

        return lifecycle.LastTerminalAttempt is { } terminal &&
               Matches(
                   terminal,
                   request,
                   releaseKind,
                   releaseAuthorityId,
                   releaseGeneration,
                   releaseOutcome);
    }

    private static bool RuntimeUnlockRetryLifecycleContainsSettlement(
        RuntimeUnlockRetryLifecycleSubstate lifecycle,
        RuntimeUnlockRetryDispatchSettlementReport report)
    {
        var history = lifecycle.AttemptHistory.IsDefault
            ? ImmutableArray<RuntimeUnlockRetryLifecycleAttemptSnapshot>.Empty
            : lifecycle.AttemptHistory;
        foreach (var attempt in history)
        {
            if (attempt.Settlement == report)
            {
                return true;
            }
        }

        return lifecycle.LastTerminalAttempt?.Settlement == report;
    }

    private static long GetRuntimeUnlockRetryDispatchDeadlineMilliseconds()
    {
        var configured =
            RuntimeUnlockRetryDispatchDeadlineOverrideForTests ??
            TimeSpan.FromMilliseconds(
                RuntimeUnlockRetryDispatchDeadlineMilliseconds);
        var milliseconds = (long)configured.TotalMilliseconds;
        return milliseconds > 0
            ? milliseconds
            : RuntimeUnlockRetryDispatchDeadlineMilliseconds;
    }

    private static RuntimeUnlockRetryLifecycleSubstate
        InvalidateRuntimeUnlockRetryLifecycle(
            RuntimeUnlockRetryLifecycleSubstate lifecycle)
    {
        var history = lifecycle.AttemptHistory;
        RuntimeUnlockRetryLifecycleAttemptSnapshot? invalidated = null;
        foreach (var attempt in new RuntimeUnlockRetryLifecycleAttemptSnapshot?[]
                 {
                     lifecycle.PendingAttempt,
                     lifecycle.InstalledAttempt,
                     lifecycle.RetainedPredecessor,
                 })
        {
            if (attempt is not { } current)
            {
                continue;
            }

            var historical = current.IsTerminal
                ? current
                : current with
                {
                    State =
                        RuntimeUnlockRetryLifecycleAttemptState.Invalidated,
                };
            history = AppendRuntimeUnlockRetryLifecycleAttemptHistory(
                history,
                historical);
            invalidated ??= historical;
        }

        invalidated ??= lifecycle.LastTerminalAttempt;
        return lifecycle with
        {
            PendingAttempt = null,
            InstalledAttempt = null,
            RetainedPredecessor = null,
            LastTerminalAttempt = invalidated,
            BoundedClosure = null,
            RepairSettlementRelease = null,
            PendingScopeInvalidationCommand = null,
            AttemptHistory = history,
        };
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

    internal static bool
        TryCreateFallbackPairedTailYieldRuntimeUnlockRetryDispatchRequest(
            FileTransferCoordinatorState state,
            FileTransferFallbackTailPairedReleaseReceipt pairedRelease,
            string retryReason,
            string recoveryReason,
            out RuntimeUnlockRetryDispatchAuthorityRequest request,
            out string rejectionReason)
    {
        var substate = state.PersistentState.FallbackTailYield;
        if (pairedRelease.IsDefault ||
            substate.CurrentOperation is not
            {
                PairedRelease: { } currentRelease,
            } ||
            currentRelease != pairedRelease ||
            substate.CurrentReservation is not null ||
            substate.PendingRetryRecheckRequest is not { } pendingRequest)
        {
            request = default;
            rejectionReason = "fallback_paired_tail_yield_pending";
            return false;
        }

        var expectedEvidence =
            CreateFallbackPairedTailYieldCompatibilityEvidence(
                pairedRelease);
        if (pendingRequest.FallbackPairedTailYieldEvidence !=
                expectedEvidence)
        {
            request = default;
            rejectionReason =
                "fallback_paired_tail_yield_retry_recheck_mismatch";
            return false;
        }

        request = pendingRequest;
        rejectionReason = "accepted";
        return true;
    }

    private static FallbackPairedTailYieldReleaseEvidence
        CreateFallbackPairedTailYieldCompatibilityEvidence(
            FileTransferFallbackTailPairedReleaseReceipt pairedRelease)
    {
        var peer = pairedRelease.PeerProof.Proof;
        return new FallbackPairedTailYieldReleaseEvidence(
            pairedRelease.LocalProof.Decision,
            peer.AuthorityId,
            peer.PredecessorAuthorityId,
            peer.YieldGeneration,
            peer.Direction.ToString().ToLowerInvariant(),
            peer.OwnerKey,
            peer.PairKey,
            peer.LiveRouteEpoch,
            peer.LegGeneration,
            peer.BridgeRecoveryGeneration,
            peer.TransportEpoch,
            peer.CheckpointRequestIdSnapshot);
    }

    internal static bool
        TryCreateFallbackPairedTailYieldRuntimeUnlockRetryDispatchRequest(
            FileTransferCoordinatorState state,
            FallbackTailYieldDecision tailYield,
            FallbackPairedTailYieldReleaseEvidence pairedYieldEvidence,
            string retryReason,
            string recoveryReason,
            out RuntimeUnlockRetryDispatchAuthorityRequest request,
            out string rejectionReason)
    {
        request = default;
        rejectionReason = "accepted";
        if (state.IsTerminal)
        {
            rejectionReason = "terminal_state";
            return false;
        }

        if (state.RouteSelection.Route !=
                FileTransferRoute.PostTunaFallbackV6 ||
            state.RouteSelection.ProtocolVersion !=
                FileTransferProtocol.ProtocolVersionV6 ||
            state.CurrentRuntimeOwner is not { } owner ||
            state.CurrentLeg is not { } leg ||
            state.CurrentRoutePublicationToken is not { } publication ||
            publication.TargetTransport != FileTransferTransportKind.RegularNkn ||
            !IsRuntimeOwnerCurrent(state, owner, out rejectionReason) ||
            !IsRoutePublicationTokenCurrent(
                state,
                publication,
                out rejectionReason))
        {
            if (string.Equals(
                    rejectionReason,
                    "accepted",
                    StringComparison.Ordinal))
            {
                rejectionReason =
                    "fallback_retry_source_publication_not_current";
            }

            return false;
        }

        if (state.PersistentState.FileTransferRouteTransition.CurrentOperation
                is not null)
        {
            rejectionReason = "filetransfer_route_transition_conflict";
            return false;
        }

        var retryLifecycle = GetRuntimeUnlockRetryLifecycle(state);
        if (retryLifecycle.CurrentAuthorizedAuthority is not null)
        {
            rejectionReason = "retry_dispatch_predecessor_active";
            return false;
        }

        if (tailYield.IsDefault ||
            GetCurrentFallbackTailYieldDecision(state) != tailYield ||
            tailYield.YieldKind != FallbackTailYieldKind.Quiescent ||
            !IsFallbackTailYieldDecisionCurrent(
                state,
                tailYield,
                out rejectionReason))
        {
            if (string.Equals(
                    rejectionReason,
                    "accepted",
                    StringComparison.Ordinal))
            {
                rejectionReason = "fallback_tail_yield_not_current";
            }

            return false;
        }

        if (!TryValidateFallbackPairedTailYieldReleaseEvidence(
                state,
                tailYield,
                pairedYieldEvidence,
                out rejectionReason))
        {
            return false;
        }

        if (state.CurrentFallbackCheckpointLease is not null ||
            state.CurrentFallbackControlPlaneToken is not null ||
            state.CurrentFallbackControlPlanePressureToken is not null)
        {
            rejectionReason = "fallback_owner_work_pending";
            return false;
        }

        var sourcePublication = CreateRoutePublicationAuthority(
            publication,
            "fallback_paired_tail_yield");
        if (sourcePublication is null)
        {
            rejectionReason = "fallback_retry_source_publication_not_current";
            return false;
        }

        request = new RuntimeUnlockRetryDispatchAuthorityRequest(
            state.SessionId,
            state.TransferId,
            state.Direction,
            sourcePublication,
            RecoveryContractGeneration: Math.Max(
                1,
                retryLifecycle.LastAuthorityGeneration + 1L),
            RetiredOfferGeneration: 0,
            DispatchAttemptGeneration: 1,
            RetryReason: NormalizeReason(retryReason),
            RecoveryReason: NormalizeReason(recoveryReason),
            RegularV4LivenessGeneration: 0,
            BridgeDecisionGeneration: 0,
            PredecessorAuthorityId: null,
            RequestActivePredecessorSupersession: false,
            MissingDispatchSuccessorGeneration: 0)
        {
            FallbackPairedTailYieldEvidence = pairedYieldEvidence,
        };
        if (!TryValidateRuntimeUnlockRetryRelease(
                state,
                request,
                RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield,
                tailYield.AuthorityId,
                tailYield.YieldGeneration,
                FallbackTailYieldKind.Quiescent.ToString(),
                out rejectionReason))
        {
            request = default;
            return false;
        }

        return true;
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

                var repairSettlementRelease =
                    GetRuntimeUnlockRetryLifecycle(state)
                        .RepairSettlementRelease;
                if (repairSettlementRelease is not { } release ||
                    release.IsDefault)
                {
                    rejectionReason = "regular_v4_recovery_settlement_consumption_pending";
                    return false;
                }

                if (release.State ==
                    RuntimeUnlockRetryLifecycleRepairSettlementReleaseState
                        .Consumed)
                {
                    rejectionReason = "regular_v4_recovery_settlement_consumption_already_used";
                    return false;
                }

                var issuedSettlement = release.Settlement;
                if (state.PersistentState.FileTransferReceiveRecovery
                        .CurrentOperation is not null ||
                    request.RegularV4LivenessGeneration != 0 ||
                    request.BridgeDecisionGeneration !=
                        issuedSettlement.BridgeDecisionGeneration ||
                    !RegularV4RepairSettlementMatchesCurrentRepair(
                        state,
                        repairDecision,
                        issuedSettlement) ||
                    issuedSettlement.AuthorityId != releaseAuthorityId ||
                    issuedSettlement.DecisionGeneration != releaseGeneration ||
                    !string.Equals(
                        issuedSettlement.Outcome.ToString(),
                        releaseOutcome,
                        StringComparison.Ordinal))
                {
                    rejectionReason = "regular_v4_recovery_settlement_consumption_stale";
                    return false;
                }

                return true;
            }

            if (releaseKind == RuntimeUnlockRetryReleaseKind.ReceiveRecoveryLeaseClosure)
            {
                return TryValidateReceiveRecoveryClosureRelease(
                    state,
                    request,
                    releaseAuthorityId,
                    releaseGeneration,
                    releaseOutcome,
                    out rejectionReason);
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
            state.PersistentState.FallbackTailYield.CurrentOperation is not
            {
                Decision:
                {
                    Accepted: true,
                    YieldKind: FallbackTailYieldKind.Quiescent,
                } tailYield,
                PairingState:
                    FileTransferFallbackTailYieldPairingState.Paired,
                PairedRelease: { IsDefault: false } pairedRelease,
            } ||
            tailYield.YieldKind != FallbackTailYieldKind.Quiescent ||
            tailYield.AuthorityId != releaseAuthorityId ||
            releaseGeneration != tailYield.YieldGeneration ||
            pairedRelease.ReleaseAuthorityId != releaseAuthorityId ||
            pairedRelease.ReleaseGeneration != releaseGeneration ||
            !string.Equals(
                pairedRelease.ReleaseOutcome,
                releaseOutcome,
                StringComparison.Ordinal))
        {
            rejectionReason = "retry_dispatch_predecessor_release_invalid";
            return false;
        }

        if (request.FallbackPairedTailYieldEvidence is not { } pairedYieldEvidence)
        {
            rejectionReason = "fallback_paired_tail_yield_pending";
            return false;
        }

        if (!TryValidateFallbackPairedTailYieldReleaseEvidence(
                state,
                tailYield,
                pairedYieldEvidence,
                out rejectionReason) ||
            !FallbackPairedTailYieldEvidenceMatchesRelease(
                pairedYieldEvidence,
                pairedRelease))
        {
            rejectionReason = "retry_dispatch_predecessor_release_invalid";
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

    private static bool TryValidateFallbackPairedTailYieldReleaseEvidence(
        FileTransferCoordinatorState state,
        FallbackTailYieldDecision tailYield,
        FallbackPairedTailYieldReleaseEvidence evidence,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        if (evidence.IsDefault)
        {
            rejectionReason = "fallback_paired_tail_yield_pending";
            return false;
        }

        if (state.CurrentRuntimeOwner is not { } owner ||
            state.CurrentLeg is not { } leg ||
            GetCurrentFallbackTailYieldDecision(state) != tailYield ||
            evidence.LocalDecision != tailYield ||
            !tailYield.Accepted ||
            tailYield.YieldKind != FallbackTailYieldKind.Quiescent ||
            tailYield.CurrentRoute != state.RouteSelection.Route ||
            tailYield.CurrentProtocolVersion != state.RouteSelection.ProtocolVersion ||
            tailYield.CurrentLiveRouteEpoch !=
                (state.CurrentLiveRouteEpoch?.EpochId ?? 0) ||
            tailYield.CurrentRuntimeGeneration != owner.RuntimeGeneration ||
            tailYield.CurrentTerminalGeneration != state.TerminalGeneration ||
            tailYield.CurrentLegGeneration != leg.Generation)
        {
            rejectionReason = "retry_dispatch_predecessor_release_invalid";
            return false;
        }

        if (!FileTransferAuthorityIdParser.TryNormalizeFallbackTailYield(
                evidence.PeerAuthorityId.ToString(),
                out _) ||
            evidence.PeerAuthorityId == tailYield.AuthorityId ||
            (evidence.PeerPredecessorAuthorityId is { IsDefault: false } predecessor &&
             (!FileTransferAuthorityIdParser.TryNormalizeFallbackTailYield(
                  predecessor.ToString(),
                  out _) ||
              predecessor == evidence.PeerAuthorityId)))
        {
            rejectionReason = "retry_dispatch_predecessor_release_invalid";
            return false;
        }

        var expectedPeerDirection =
            state.Direction == FileTransferDirection.Outbound
                ? FileTransferDirection.Inbound.ToString().ToLowerInvariant()
                : FileTransferDirection.Outbound.ToString().ToLowerInvariant();
        var expectedPeerOwnerKey = BuildFallbackTailYieldOwnerKey(
            state.SessionId,
            state.TransferId,
            tailYield.CurrentRoute == FileTransferRoute.PostTunaFallbackV6
                ? FileTransferRouteResolver.PostTunaFallbackV6Token
                : state.RouteSelection.TelemetryToken,
            tailYield.CurrentProtocolVersion,
            evidence.PeerLiveRouteEpoch,
            evidence.PeerLegGeneration,
            evidence.PeerBridgeRecoveryGeneration,
            evidence.PeerTransportEpoch,
            evidence.PeerCheckpointRequestIdSnapshot);
        var expectedPeerPairKey = BuildFallbackTailYieldPairKey(
            state.SessionId,
            state.TransferId,
            tailYield.CurrentRoute == FileTransferRoute.PostTunaFallbackV6
                ? FileTransferRouteResolver.PostTunaFallbackV6Token
                : state.RouteSelection.TelemetryToken,
            tailYield.CurrentProtocolVersion,
            evidence.PeerLiveRouteEpoch,
            evidence.PeerTransportEpoch,
            evidence.PeerCheckpointRequestIdSnapshot);
        if (!string.Equals(
                evidence.PeerDirection,
                expectedPeerDirection,
                StringComparison.Ordinal) ||
            evidence.PeerLiveRouteEpoch != tailYield.CurrentLiveRouteEpoch ||
            !string.Equals(
                evidence.PeerCheckpointRequestIdSnapshot,
                tailYield.CheckpointRequestIdSnapshot,
                StringComparison.Ordinal) ||
            !string.Equals(
                evidence.PeerOwnerKey,
                expectedPeerOwnerKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                evidence.PeerPairKey,
                expectedPeerPairKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                evidence.PeerPairKey,
                tailYield.PairKey,
                StringComparison.Ordinal))
        {
            rejectionReason = "retry_dispatch_predecessor_release_invalid";
            return false;
        }

        return true;
    }

    private static bool FallbackPairedTailYieldEvidenceMatchesRelease(
        FallbackPairedTailYieldReleaseEvidence evidence,
        FileTransferFallbackTailPairedReleaseReceipt release)
    {
        var peer = release.PeerProof.Proof;
        return evidence.LocalDecision == release.LocalProof.Decision &&
               evidence.PeerAuthorityId == peer.AuthorityId &&
               evidence.PeerPredecessorAuthorityId ==
                   peer.PredecessorAuthorityId &&
               evidence.PeerYieldGeneration == peer.YieldGeneration &&
               string.Equals(
                   evidence.PeerDirection,
                   peer.Direction.ToString().ToLowerInvariant(),
                   StringComparison.Ordinal) &&
               string.Equals(
                   evidence.PeerOwnerKey,
                   peer.OwnerKey,
                   StringComparison.Ordinal) &&
               string.Equals(
                   evidence.PeerPairKey,
                   peer.PairKey,
                   StringComparison.Ordinal) &&
               evidence.PeerLiveRouteEpoch == peer.LiveRouteEpoch &&
               evidence.PeerLegGeneration == peer.LegGeneration &&
               evidence.PeerBridgeRecoveryGeneration ==
                   peer.BridgeRecoveryGeneration &&
               evidence.PeerTransportEpoch == peer.TransportEpoch &&
               string.Equals(
                   evidence.PeerCheckpointRequestIdSnapshot,
                   peer.CheckpointRequestIdSnapshot,
                   StringComparison.Ordinal);
    }

    private static bool IsEligibleRuntimeUnlockRetryRepairSettlementRelease(
        FileTransferCoordinatorState state,
        FileTransferRegularV4FrontierRepairSettlement settlement)
        => settlement.Outcome is (
               FileTransferRegularV4FrontierRepairSettlementOutcome
                   .ProgressObserved or
               FileTransferRegularV4FrontierRepairSettlementOutcome
                   .MissingDispatch or
               FileTransferRegularV4FrontierRepairSettlementOutcome
                   .SettledStalled) &&
           state.PersistentState.FileTransferReceiveRecovery.CurrentOperation
               is null &&
           state.CurrentRepairReplayDecision is { } repairDecision &&
           IsRepairReplayDecisionCurrent(state, repairDecision, out _) &&
           RegularV4RepairSettlementMatchesCurrentRepair(
               state,
               repairDecision,
               settlement);

    private static bool RegularV4RepairSettlementMatchesCurrentRepair(
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
            if (GetRuntimeUnlockRetryLifecycle(state)
                    .RepairSettlementRelease is not
                {
                    State:
                        RuntimeUnlockRetryLifecycleRepairSettlementReleaseState
                            .Consumed,
                    ConsumingAuthority: { IsDefault: false },
                    ConsumingInstallReceipt: { IsDefault: false },
                } repairSettlementRelease ||
                repairSettlementRelease.IsDefault ||
                repairSettlementRelease.Settlement.AuthorityId !=
                    releaseAuthorityId ||
                repairSettlementRelease.Settlement.DecisionGeneration !=
                    releaseGeneration ||
                !string.Equals(
                    repairSettlementRelease.Settlement.Outcome.ToString(),
                    releaseOutcome,
                    StringComparison.Ordinal))
            {
                rejectionReason = "retry_dispatch_predecessor_recovery_handoff_stale";
                return false;
            }
        }
        else if (releaseKind ==
                 RuntimeUnlockRetryReleaseKind.ReceiveRecoveryLeaseClosure)
        {
            var recoveryState =
                state.PersistentState.FileTransferReceiveRecovery;
            var operation =
                recoveryState.CurrentOperation ??
                recoveryState.LastTerminalOperation;
            if (operation is not { } recoveryOperation ||
                recoveryOperation.Authority.OperationGeneration !=
                    expectedPredecessor
                        .ReceiveRecoveryOperationGeneration ||
                recoveryOperation.Authority.Observation
                        .DataSessionGeneration !=
                    expectedPredecessor
                        .ReceiveRecoveryDataSessionGeneration ||
                recoveryOperation.RetiredRecoveryLivenessLease is not
                    { } retiredLease ||
                retiredLease.AuthorityId != releaseAuthorityId ||
                retiredLease.LeaseGeneration != releaseGeneration)
            {
                rejectionReason =
                    "retry_dispatch_predecessor_recovery_handoff_stale";
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
        FileTransferTransportKind? targetTransport,
        long? targetTransportEpoch)
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
            targetTransportEpoch is > 0
                ? targetTransportEpoch.Value
                : leg.TransportEpochId,
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
        if (state.PersistentState.FallbackTailYield.CurrentOperation is not
                { } sourceOperation ||
            sourceOperation.Decision != predecessor ||
            !sourceOperation.Scope.HasCompleteExecutionScope)
        {
            rejectionReason =
                "fallback_tail_yield_transport_epoch_reauthorization_source_missing";
            return false;
        }

        var targetTransportDecision =
            request.TargetTransportEpochDecision;
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
            state.CurrentTransportEpochDecision !=
                targetTransportDecision ||
            !targetTransportDecision.Accepted ||
            targetTransportDecision.ActionKind !=
                TransportEpochActionKind.Adopt ||
            targetTransportDecision.CurrentRoute !=
                sourceOperation.Scope.Route ||
            targetTransportDecision.CurrentProtocolVersion !=
                sourceOperation.Scope.ProtocolVersion ||
            targetTransportDecision.CurrentLiveRouteEpoch !=
                sourceOperation.Scope.LiveRouteEpoch ||
            targetTransportDecision.CurrentRuntimeGeneration !=
                sourceOperation.Scope.RuntimeOwner.RuntimeGeneration ||
            targetTransportDecision.CurrentTerminalGeneration !=
                sourceOperation.Scope.TerminalGeneration ||
            targetTransportDecision.CurrentLegGeneration !=
                sourceOperation.Scope.LegGeneration ||
            targetTransportDecision.TransportEpoch !=
                request.SourceTransportEpoch ||
            targetTransportDecision.TargetTransportEpoch !=
                request.TargetTransportEpoch ||
            targetTransportDecision.BridgeRecoveryGeneration !=
                sourceOperation.Scope.BridgeRecoveryGeneration ||
            targetTransportDecision.RuntimeOwner !=
                request.SourceRuntimeOwner ||
            targetTransportDecision.TargetRoute !=
                FileTransferRoute.PostTunaFallbackV6 ||
            targetTransportDecision.TargetProtocolVersion !=
                FileTransferProtocol.ProtocolVersionV6 ||
            targetTransportDecision.TargetTransport !=
                FileTransferTransportKind.RegularNkn ||
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
            predecessor.CheckpointLease !=
                state.CurrentFallbackCheckpointLease ||
            sourceOperation.Scope.CheckpointGeneration !=
                sourceLeg.CheckpointGeneration ||
            sourceOperation.Scope.DataSessionGeneration !=
                state.CurrentDataSessionGeneration ||
            !string.Equals(
                sourceOperation.Scope.DataSessionIdentity,
                state.CurrentDataSessionIdentity,
                StringComparison.Ordinal) ||
            !string.Equals(predecessor.OwnerKey, expectedSourceOwnerKey, StringComparison.Ordinal) ||
            !string.Equals(predecessor.PairKey, expectedSourcePairKey, StringComparison.Ordinal) ||
            (sourceLeg.TransportEpochId != request.SourceTransportEpoch &&
             sourceLeg.TransportEpochId != request.TargetTransportEpoch) ||
            coordinatorEvent.TransportEpochId != request.TargetTransportEpoch ||
            coordinatorEvent.BridgeRecoveryGeneration != sourceLeg.BridgeRecoveryGeneration ||
            HasFallbackTailPairedReleaseForDecision(
                state,
                predecessor))
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
            request.TargetTransportEpochDecision,
            targetOwner,
            targetState.TerminalGeneration,
            targetLeg.Generation,
            targetLeg.BridgeRecoveryGeneration,
            targetState.CurrentRoutePublicationToken,
            targetState.CurrentBridgeRecoveryDecision,
            targetState.CurrentDataSessionIdentity,
            targetState.CurrentDataSessionGeneration,
            NormalizeFallbackTailYieldCheckpointRequestId(targetState.CurrentFallbackCheckpointLease),
            targetLeg.CheckpointGeneration,
            targetState.CurrentFallbackCheckpointLease,
            Math.Max(
                1,
                GetLastFallbackTailYieldTransportEpochReauthorizationGeneration(
                    sourceState) + 1),
            NormalizeReason(request.Reason));
    }

    private static FileTransferCoordinatorDecision StartTransfer(
        FileTransferCoordinatorEvent coordinatorEvent,
        FileTransferCoordinatorState state,
        string reason)
    {
        if (state.PersistentState.FileTransferRouteTransition.CurrentOperation
            is not null)
        {
            return MutationRejected(
                state,
                reason,
                "filetransfer_route_transition_invalidation_required");
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

        if (RequiresRuntimeUnlockRetryLifecycleScopeInvalidation(state))
        {
            return MutationRejected(
                state,
                reason,
                "runtime_unlock_retry_scope_invalidation_required",
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
            var fallbackTail =
                nextState.PersistentState.FallbackTailYield;
            nextState = ApplyFallbackTailYieldSubstate(
                nextState,
                fallbackTail with
            {
                    PendingReauthorization = pendingReauthorization,
                    PendingReauthorizationPeerProof = null,
                    RetainedPredecessor =
                        pendingReauthorization.PredecessorDecision,
                    LastReauthorizationGeneration = Math.Max(
                        fallbackTail.LastReauthorizationGeneration,
                        pendingReauthorization
                            .ReauthorizationGeneration),
                });
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
        if (state.PersistentState.FileTransferRouteTransition.CurrentOperation
                is not null)
        {
            return MutationRejected(
                state,
                reason,
                "filetransfer_route_transition_commit_required");
        }

        if (HasRegularV4FrontierRepairPressureBinding(state))
        {
            return MutationRejected(
                state,
                reason,
                "regular_v4_frontier_repair_pressure_retirement_required");
        }

        if (RequiresRuntimeUnlockRetryLifecycleScopeInvalidation(state))
        {
            return MutationRejected(
                state,
                reason,
                "runtime_unlock_retry_scope_invalidation_required");
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

        if (coordinatorEvent.TransportEpochId > 0 &&
            coordinatorEvent.TransportEpochId != leg.TransportEpochId &&
            RequiresRuntimeUnlockRetryLifecycleScopeInvalidation(state))
        {
            return MutationRejected(
                state,
                reason,
                "runtime_unlock_retry_scope_invalidation_required");
        }

        var previousTransportEpoch = leg.TransportEpochId;
        var previousBridgeRecoveryGeneration =
            leg.BridgeRecoveryGeneration;
        var previousCheckpointRequestId = leg.CheckpointRequestId;
        var previousCheckpointGeneration = leg.CheckpointGeneration;
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
        if (leg.TransportEpochId != previousTransportEpoch ||
            leg.BridgeRecoveryGeneration !=
                previousBridgeRecoveryGeneration ||
            !string.Equals(
                leg.CheckpointRequestId,
                previousCheckpointRequestId,
                StringComparison.Ordinal) ||
            leg.CheckpointGeneration != previousCheckpointGeneration)
        {
            nextState = InvalidateRecoveryAuthority(nextState);
        }
        nextState = RebindPendingFallbackTailYieldCheckpointScope(
            nextState,
            "fallback_checkpoint_requested");

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

        if (coordinatorEvent.TransportEpochId > 0 &&
            coordinatorEvent.TransportEpochId != leg.TransportEpochId &&
            RequiresRuntimeUnlockRetryLifecycleScopeInvalidation(state))
        {
            return MutationRejected(
                state,
                reason,
                "runtime_unlock_retry_scope_invalidation_required");
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
        var previousBridgeRecoveryGeneration =
            leg.BridgeRecoveryGeneration;
        var previousCheckpointRequestId = leg.CheckpointRequestId;
        var previousCheckpointGeneration = leg.CheckpointGeneration;
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
        if (leg.TransportEpochId != previousTransportEpoch ||
            leg.BridgeRecoveryGeneration !=
                previousBridgeRecoveryGeneration ||
            !string.Equals(
                leg.CheckpointRequestId,
                previousCheckpointRequestId,
                StringComparison.Ordinal) ||
            leg.CheckpointGeneration != previousCheckpointGeneration)
        {
            nextState = InvalidateRecoveryAuthority(nextState);
        }
        nextState = RebindPendingFallbackTailYieldCheckpointScope(
            nextState,
            "fallback_checkpoint_accepted");

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

        var previousCheckpointRequestId = leg!.CheckpointRequestId;
        var previousCheckpointGeneration = leg.CheckpointGeneration;
        RetireFallbackCheckpointRequest(leg);
        var retiredLease = currentLease with { State = FallbackCheckpointLeaseState.Retired, Reason = reason };
        var nextState = state with
        {
            CurrentFallbackCheckpointLease = null,
        };
        if (!string.Equals(
                leg.CheckpointRequestId,
                previousCheckpointRequestId,
                StringComparison.Ordinal) ||
            leg.CheckpointGeneration != previousCheckpointGeneration)
        {
            nextState = InvalidateRecoveryAuthority(nextState);
        }
        nextState = RebindPendingFallbackTailYieldCheckpointScope(
            nextState,
            "fallback_checkpoint_retired");
        return Decision(
            nextState,
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

        if (coordinatorEvent.TransportEpochId > 0 &&
            coordinatorEvent.TransportEpochId != leg.TransportEpochId &&
            RequiresRuntimeUnlockRetryLifecycleScopeInvalidation(state))
        {
            return MutationRejected(
                state,
                reason,
                "runtime_unlock_retry_scope_invalidation_required");
        }

        var reauthorization =
            coordinatorEvent.FallbackTailYieldTransportEpochReauthorization;
        var requestedPredecessor =
            reauthorization?.PredecessorDecision.AuthorityId;
        var hasExactCurrentYield =
            TryGetExactCurrentFallbackTailYieldDecision(
                state,
                FallbackTailYieldKind.Quiescent,
                requestedPredecessor,
                reauthorization,
                out var exactCurrentYield);
        if (!hasExactCurrentYield &&
            state.PersistentState.FallbackTailYield.CurrentOperation is
                { } currentOperation &&
            IsFallbackTailYieldScopeCurrent(
                state,
                currentOperation.Scope,
                out _))
        {
            return MutationRejected(
                state,
                reason,
                "fallback_tail_yield_operation_conflict");
        }

        if (!hasExactCurrentYield &&
            reauthorization is { } suppliedReauthorization)
        {
            if (!IsFallbackTailYieldTransportEpochReauthorizationCurrent(
                    state,
                    suppliedReauthorization,
                    out var reauthorizationRejectionReason))
            {
                return MutationRejected(state, reason, reauthorizationRejectionReason);
            }
        }
        else if (!hasExactCurrentYield &&
                 GetPendingFallbackTailYieldTransportEpochReauthorization(
                     state) is not null)
        {
            return MutationRejected(
                state,
                reason,
                "fallback_tail_yield_transport_epoch_reauthorization_missing");
        }

        if (!hasExactCurrentYield &&
            (state.CurrentRuntimeOwner is null ||
             state.CurrentRoutePublicationToken is not
                 { IsDefault: false } ||
             string.IsNullOrWhiteSpace(
                 state.CurrentDataSessionIdentity) ||
             state.CurrentDataSessionGeneration <= 0))
        {
            return MutationRejected(
                state,
                reason,
                "fallback_tail_yield_execution_scope_incomplete");
        }

        var previousTransportEpoch = leg.TransportEpochId;
        var previousBridgeRecoveryGeneration =
            leg.BridgeRecoveryGeneration;
        var previousCheckpointRequestId = leg.CheckpointRequestId;
        var previousCheckpointGeneration = leg.CheckpointGeneration;
        MarkFallbackTailQuiescent(
            leg,
            coordinatorEvent.TransportEpochId,
            coordinatorEvent.CommittedChunkIndex,
            coordinatorEvent.HighestObservedChunkIndex);
        var yieldGeneration = Math.Max(
            1,
            GetLastFallbackTailYieldGeneration(state) + 1);
        var tailState = state with
        {
            CurrentFallbackCheckpointLease = null,
        };
        var yieldDecision = hasExactCurrentYield
            ? exactCurrentYield
            : CreateFallbackTailYieldDecision(
                tailState,
                accepted: true,
                FallbackTailYieldKind.Quiescent,
                reason,
                yieldGeneration,
                predecessorAuthorityId: requestedPredecessor);
        var nextState = hasExactCurrentYield
            ? tailState
            : ApplyIssuedFallbackTailYieldDecision(
                tailState,
                yieldDecision,
                "fallback_tail_yield_successor_issued",
                reauthorization);
        if (leg.TransportEpochId != previousTransportEpoch ||
            leg.BridgeRecoveryGeneration !=
                previousBridgeRecoveryGeneration ||
            !string.Equals(
                leg.CheckpointRequestId,
                previousCheckpointRequestId,
                StringComparison.Ordinal) ||
            leg.CheckpointGeneration != previousCheckpointGeneration)
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

        if (TryGetExactCurrentFallbackTailYieldDecision(
                state,
                FallbackTailYieldKind.Failed,
                predecessorAuthorityId: null,
                sourceReauthorization: null,
                out var exactCurrentYield))
        {
            MarkFallbackTailFailed(leg!);
            return Decision(
                state,
                reason,
                mutatedLeg: leg,
                fallbackTailYieldDecision: exactCurrentYield);
        }

        if (state.CurrentRuntimeOwner is null ||
            state.CurrentRoutePublicationToken is not
                { IsDefault: false } ||
            string.IsNullOrWhiteSpace(state.CurrentDataSessionIdentity) ||
            state.CurrentDataSessionGeneration <= 0)
        {
            return MutationRejected(
                state,
                reason,
                "fallback_tail_yield_execution_scope_incomplete");
        }

        if (state.PersistentState.FallbackTailYield.CurrentOperation is
                { } currentOperation &&
            IsFallbackTailYieldScopeCurrent(
                state,
                currentOperation.Scope,
                out _))
        {
            return MutationRejected(
                state,
                reason,
                "fallback_tail_yield_operation_conflict");
        }

        MarkFallbackTailFailed(leg!);
        var yieldGeneration = Math.Max(
            1,
            GetLastFallbackTailYieldGeneration(state) + 1);
        var yieldDecision = CreateFallbackTailYieldDecision(
            state,
            accepted: true,
            FallbackTailYieldKind.Failed,
            reason,
            yieldGeneration);
        return Decision(
            ApplyIssuedFallbackTailYieldDecision(
                state,
                yieldDecision,
                "fallback_tail_yield_failed"),
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
        if (state.PersistentState.FileTransferRouteTransition.CurrentOperation
            is not null)
        {
            return MutationRejected(
                state,
                reason,
                "filetransfer_route_transition_invalidation_required");
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
        if (state.PersistentState.FileTransferRouteTransition.CurrentOperation
            is not null)
        {
            return MutationRejected(
                state,
                reason,
                "filetransfer_route_transition_invalidation_required");
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
        if (state.PersistentState.FileTransferRouteTransition.CurrentOperation
            is not null)
        {
            return MutationRejected(
                state,
                reason,
                "filetransfer_route_transition_invalidation_required");
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
        if (state.PersistentState.FileTransferRouteTransition.CurrentOperation
            is not null)
        {
            return MutationRejected(
                state,
                reason,
                "filetransfer_route_transition_invalidation_required");
        }

        if (HasRegularV4FrontierRepairPressureBinding(state))
        {
            return MutationRejected(
                state,
                reason,
                "regular_v4_frontier_repair_pressure_retirement_required");
        }

        var nextState = state.PersistentState.FileTransferReceiveRecovery
                .CurrentOperation is null
            ? state
            : InvalidateRecoveryAuthority(state);
        nextState = nextState with
            {
                CurrentRuntimeUnlockAdmission = null,
                CurrentRouteCommit = null,
                CurrentRoutePublicationToken = null,
                CurrentRuntimeUnlockPendingPreCommitProbe = null,
            };
        nextState = InvalidateFallbackTailYieldSubstate(
            nextState,
            "runtime_unlock_admission_retired");
        return Decision(
            nextState,
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
        if (!string.Equals(
                coordinatorEvent.RouteSelection.TelemetryToken,
                expectedRoute.TelemetryToken,
                StringComparison.Ordinal))
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
        if (state.PersistentState.FileTransferRouteTransition.CurrentOperation
            is not null)
        {
            return MutationRejected(
                state,
                reason,
                "filetransfer_route_transition_invalidation_required");
        }

        if (HasRegularV4FrontierRepairPressureBinding(state))
        {
            return MutationRejected(
                state,
                reason,
                "regular_v4_frontier_repair_pressure_retirement_required");
        }

        if (RequiresRuntimeUnlockRetryLifecycleScopeInvalidation(state))
        {
            return MutationRejected(
                state,
                reason,
                "runtime_unlock_retry_scope_invalidation_required");
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

    private static FileTransferCoordinatorState
        ApplyIssuedRepairOrTransportAuthority(
        FileTransferCoordinatorState state,
        RepairReplayDecision? repairReplayDecision,
        TransportEpochDecision? transportEpochDecision)
    {
        var next = state;
        if (repairReplayDecision is { } repair)
        {
            var retryLifecycle =
                GetRuntimeUnlockRetryLifecycle(next) with
                {
                    RepairSettlementRelease = null,
                };
            next = ApplyRuntimeUnlockRetryLifecycle(
                next with
                {
                    CurrentRepairReplayDecision = repair,
                    LastRepairReplayGeneration = repair.RepairGeneration,
                },
                retryLifecycle);
        }

        if (transportEpochDecision is { } transportEpoch)
        {
            next = next with
            {
                CurrentTransportEpochDecision = transportEpoch,
                LastTransportEpochDecisionGeneration = transportEpoch.TransportDecisionGeneration,
            };
        }

        return next;
    }

    private static FileTransferCoordinatorState
        ApplyReceiveRecoverySubstate(
            FileTransferCoordinatorState state,
            FileTransferReceiveRecoverySubstate recoveryState)
        => state with
        {
            PersistentState = state.PersistentState with
            {
                FileTransferReceiveRecovery = recoveryState,
            },
        };

    private static FileTransferCoordinatorState
        ApplyIssuedReceiveRecoveryOperation(
            FileTransferCoordinatorState state,
            FileTransferReceiveRecoveryOperationAuthority authority)
    {
        var next = state;
        var bridgeRecovery = authority.BridgeRecoveryDecision;
        if (GetPendingFallbackTailYieldTransportEpochReauthorization(next) is
                { } pending &&
            pending.TargetBridgeRecoveryDecision != bridgeRecovery)
        {
            if (CanBindFallbackTailYieldTransportEpochReauthorizationBridgeDecision(
                    next,
                    pending,
                    bridgeRecovery))
            {
                var reauthorizationGeneration = Math.Max(
                    pending.ReauthorizationGeneration + 1,
                    GetLastFallbackTailYieldTransportEpochReauthorizationGeneration(
                        next) + 1);
                var fallbackTail =
                    next.PersistentState.FallbackTailYield;
                next = ApplyFallbackTailYieldSubstate(
                    next,
                    fallbackTail with
                    {
                        PendingReauthorization = pending with
                        {
                            TargetBridgeRecoveryDecision = bridgeRecovery,
                            ReauthorizationGeneration =
                                reauthorizationGeneration,
                            Reason =
                                NormalizeReason(bridgeRecovery.Reason),
                        },
                        PendingReauthorizationPeerProof = null,
                        LastReauthorizationGeneration =
                            reauthorizationGeneration,
                    });
            }
            else
            {
                next =
                    InvalidateFallbackTailYieldTransportEpochReauthorizationState(
                    next,
                    pending,
                    "bridge_recovery_decision_replaced");
            }
        }

        next = ApplyRuntimeUnlockRetryLifecycle(
            next,
            GetRuntimeUnlockRetryLifecycle(next) with
            {
                RepairSettlementRelease = null,
            });

        var recoveryState =
            next.PersistentState.FileTransferReceiveRecovery;
        var livenessDeadlineUtcMs =
            AdvanceReceiveRecoveryLivenessDeadline(
                createdUtcMs: authority.Observation.ObservedUtcMs,
                currentDeadlineUtcMs: 0,
                observedUtcMs: authority.Observation.ObservedUtcMs);
        var issuedState = next with
        {
            PersistentState = next.PersistentState with
            {
                FileTransferReceiveRecovery = recoveryState with
                {
                    CurrentOperation =
                        new FileTransferReceiveRecoveryOperation(
                            authority,
                            FileTransferReceiveRecoveryExecutionState.Issued,
                            LivenessDeferralDeadlineUtcMs:
                                livenessDeadlineUtcMs,
                            LivenessDeferralGrant:
                                CreateReceiveRecoveryLivenessDeferralGrant(
                                    authority,
                                    livenessDeadlineUtcMs)),
                    LastOperationGeneration = Math.Max(
                        recoveryState.LastOperationGeneration,
                        authority.OperationGeneration),
                    ProofStatePublication = null,
                },
            },
        };
        return issuedState;
    }

    private static FileTransferRecoveryLivenessDeferralGrant
        CreateReceiveRecoveryLivenessDeferralGrant(
            FileTransferReceiveRecoveryOperationAuthority authority,
            long deadlineUtcMs,
            RecoveryLivenessLease? projectedLease = null)
    {
        var observation = authority.Observation;
        var lease = projectedLease ?? authority.RecoveryLivenessLease;
        return lease is { } exactLease
            ? new FileTransferRecoveryLivenessDeferralGrant(
                exactLease.SessionId,
                exactLease.TransferId,
                exactLease.Direction,
                exactLease.RouteToken,
                exactLease.ProtocolVersion,
                exactLease.LiveRouteEpoch,
                exactLease.RuntimeGeneration,
                exactLease.TerminalGeneration,
                exactLease.LegGeneration,
                exactLease.TransportEpoch,
                exactLease.BridgeRecoveryGeneration,
                exactLease.LeaseGeneration,
                exactLease.State.ToString(),
                exactLease.Reason,
                DateTimeOffset.FromUnixTimeMilliseconds(deadlineUtcMs))
            : new FileTransferRecoveryLivenessDeferralGrant(
                observation.SessionId,
                observation.TransferId,
                observation.Direction,
                observation.RouteToken,
                observation.ProtocolVersion,
                observation.LiveRouteEpoch,
                observation.RuntimeOwner.RuntimeGeneration,
                observation.TerminalGeneration,
                observation.LegGeneration,
                observation.TransportEpoch,
                observation.BridgeRecoveryGeneration,
                authority.BridgeRecoveryDecision.BridgeDecisionGeneration,
                "ReceiveRecoveryOperation",
                observation.Reason,
                DateTimeOffset.FromUnixTimeMilliseconds(deadlineUtcMs));
    }

    private static long AdvanceReceiveRecoveryLivenessDeadline(
        FileTransferReceiveRecoveryOperation operation,
        long observedUtcMs)
        => AdvanceReceiveRecoveryLivenessDeadline(
            operation.Authority.Observation.ObservedUtcMs,
            operation.LivenessDeferralDeadlineUtcMs,
            observedUtcMs);

    private static long AdvanceReceiveRecoveryLivenessDeadline(
        long createdUtcMs,
        long currentDeadlineUtcMs,
        long observedUtcMs)
    {
        if (createdUtcMs <= 0)
        {
            return currentDeadlineUtcMs;
        }

        if (observedUtcMs <= 0)
        {
            return currentDeadlineUtcMs;
        }

        var effectiveObservedUtcMs = Math.Max(createdUtcMs, observedUtcMs);
        var maximumDeadlineUtcMs = AddMillisecondsSaturating(
            createdUtcMs,
            ReceiveRecoveryLivenessMaxDeferralMs);
        var rollingDeadlineUtcMs = Math.Min(
            AddMillisecondsSaturating(
                effectiveObservedUtcMs,
                ReceiveRecoveryLivenessDeferralMs),
            maximumDeadlineUtcMs);
        return Math.Max(currentDeadlineUtcMs, rollingDeadlineUtcMs);
    }

    private static long AddMillisecondsSaturating(long utcMs, long durationMs)
        => utcMs > long.MaxValue - durationMs
            ? long.MaxValue
            : utcMs + durationMs;

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
               string.Equals(
                   state.CurrentDataSessionIdentity,
                   pending.TargetDataSessionIdentity,
                   StringComparison.Ordinal) &&
               state.CurrentDataSessionGeneration ==
                   pending.TargetDataSessionGeneration &&
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
                 GetPendingFallbackTailYieldTransportEpochReauthorization(state) is not null)
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
           IsTransportEpochAuthorityAction(kind);

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

    private static bool IsRecoveryAuthorityAction(FileTransferCoordinatorActionKind kind)
        => kind is FileTransferCoordinatorActionKind.RepairReplay or
                   FileTransferCoordinatorActionKind.TransportEpochAdoption or
                   FileTransferCoordinatorActionKind.PeerEpochPromotion or
                   FileTransferCoordinatorActionKind.UnmatchedFrontierProof or
                   FileTransferCoordinatorActionKind.RouteMismatchMark;

    private static bool IsTransportEpochAuthorityAction(FileTransferCoordinatorActionKind kind)
        => kind is FileTransferCoordinatorActionKind.TransportEpochAdoption or
                   FileTransferCoordinatorActionKind.PeerEpochPromotion or
                   FileTransferCoordinatorActionKind.UnmatchedFrontierProof or
                   FileTransferCoordinatorActionKind.RouteMismatchMark;

    private static TransportEpochActionKind DefaultTransportEpochActionKind(FileTransferCoordinatorActionKind kind)
        => kind switch
        {
            FileTransferCoordinatorActionKind.PeerEpochPromotion => TransportEpochActionKind.PromotePeerEpoch,
            FileTransferCoordinatorActionKind.RouteMismatchMark => TransportEpochActionKind.MarkRouteMismatch,
            FileTransferCoordinatorActionKind.UnmatchedFrontierProof => TransportEpochActionKind.RecoverLiveRoute,
            _ => TransportEpochActionKind.Adopt,
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
                CreateFileTransferRouteTransitionPhysicalDrainFingerprint(
                    capturedPhysicalSendLeaseIds),
                StringComparison.Ordinal))
        {
            rejectionReason = "filetransfer_route_transition_drain_boundary_invalid";
            return false;
        }

        long previous = 0;
        foreach (var leaseId in capturedPhysicalSendLeaseIds)
        {
            if (leaseId <= previous ||
                leaseId > capturedThroughPhysicalSendGeneration)
            {
                rejectionReason = "filetransfer_route_transition_drain_lease_set_invalid";
                return false;
            }

            previous = leaseId;
        }

        return true;
    }

    private static bool IsExactFileTransferRouteTransitionPressureReceiptDuplicate(
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

    private static bool IsCanonicalFileTransferRouteTransitionPressureSettlementReceipt(
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
        CreateFileTransferRouteTransitionPressureSettlement(
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
            "filetransfer_route_transition_pressure_handoff",
            FileTransferRegularV4FrontierRepairSettlementOutcome.StaleRejected,
            authority.TargetTransport)
        {
            AuthorityId = authority.AuthorityId,
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
                 FileTransferRouteTransitionKind.TunaV4ToPostTunaFallbackV6)
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
                    FallbackTailPairedReleaseReceipt:
                    {
                        IsDefault: false,
                    } pairedRelease,
                } ||
                receipt.Authority.ReleaseKind !=
                    RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield ||
                receipt.Authority.ReleaseAuthorityId !=
                    pairedRelease.ReleaseAuthorityId ||
                receipt.Authority.ReleaseGeneration !=
                    pairedRelease.ReleaseGeneration ||
                !string.Equals(
                    receipt.Authority.ReleaseOutcome,
                    pairedRelease.ReleaseOutcome,
                    StringComparison.Ordinal) ||
                !TryValidateFileTransferFallbackTunaActivationBoundaryCurrent(
                    state,
                    receipt,
                    pairedRelease,
                    request.ProofBoundary as
                        FileTransferTunaActivationTransitionProofBoundary,
                    out rejectionReason))
            {
                if (string.Equals(
                        rejectionReason,
                        "accepted",
                        StringComparison.Ordinal))
                {
                    rejectionReason =
                        "filetransfer_route_transition_fallback_release_boundary_invalid";
                }

                return false;
            }
        }

        if (request.Kind is
                FileTransferRouteTransitionKind.TunaV4ToPostTunaFallbackV6 or
                FileTransferRouteTransitionKind.UncommittedTunaToPostTunaFallbackV6 &&
            (request.ProofBoundary is not FileTransferFallbackTransitionProofBoundary fallback ||
             !TryValidateFileTransferFallbackProofBoundary(
                 state,
                 request,
                 fallback,
                 out rejectionReason)))
        {
            if (string.Equals(rejectionReason, "accepted", StringComparison.Ordinal))
            {
                rejectionReason = "filetransfer_route_transition_peer_proof_missing";
            }

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
                request.SourceRouteCommit is not null &&
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
                request.SourceRouteCommit is null &&
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

    private static bool TryValidateFileTransferFallbackProofBoundary(
        FileTransferCoordinatorState state,
        FileTransferRouteTransitionPrepareRequest request,
        FileTransferFallbackTransitionProofBoundary fallback,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        var evidence = fallback.PeerProofEvidence;
        if (fallback.IsDefault ||
            !evidence.IsComplete ||
            !evidence.IsExactFrameBound ||
            !string.Equals(evidence.SessionId, state.SessionId, StringComparison.Ordinal) ||
            !string.Equals(evidence.TransferId, state.TransferId, StringComparison.Ordinal) ||
            evidence.Direction != state.Direction ||
            evidence.FrameIdentity != fallback.RetainedFrameIdentity ||
            evidence.TransportEpoch != fallback.TargetTransportEpoch ||
            fallback.TargetTransportEpoch <=
                request.DurableCheckpoint.SourceTransportEpoch ||
            fallback.TargetBridgeRecoveryGeneration <= 0 ||
            fallback.SourceRouteCommit != request.SourceRouteCommit)
        {
            rejectionReason = "filetransfer_route_transition_peer_proof_mismatch";
            return false;
        }

        if (request.Kind ==
            FileTransferRouteTransitionKind.TunaV4ToPostTunaFallbackV6)
        {
            if (fallback.SourceRouteCommit is not { } sourceCommit ||
                state.CurrentRouteCommit != sourceCommit ||
                fallback.PredecessorTransitionAuthorityId is not null ||
                request.PredecessorTransitionAuthorityId is not null)
            {
                rejectionReason =
                    "filetransfer_route_transition_tuna_fallback_lineage_invalid";
                return false;
            }

            return true;
        }

        if (request.Kind !=
                FileTransferRouteTransitionKind
                    .UncommittedTunaToPostTunaFallbackV6 ||
            fallback.SourceRouteCommit is not null ||
            request.PredecessorTransitionAuthorityId is not
                { IsDefault: false } predecessor ||
            fallback.PredecessorTransitionAuthorityId != predecessor ||
            state.PersistentState.FileTransferRouteTransition.CurrentOperation is not
                { } current ||
            current.Authority.AuthorityId != predecessor ||
            current.Authority.Kind !=
                FileTransferRouteTransitionKind.RegularV4ToTunaV4)
        {
            rejectionReason =
                "filetransfer_route_transition_uncommitted_fallback_lineage_invalid";
            return false;
        }

        return true;
    }

    private static bool
        TryValidateFileTransferFallbackTunaActivationBoundaryCurrent(
            FileTransferCoordinatorState state,
            RuntimeUnlockRetryDispatchInstallReceipt installReceipt,
            FileTransferFallbackTailPairedReleaseReceipt pairedRelease,
            FileTransferTunaActivationTransitionProofBoundary? activation,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        var retryLifecycle = GetRuntimeUnlockRetryLifecycle(state);
        if (retryLifecycle.PendingScopeInvalidationCommand is not null)
        {
            rejectionReason =
                "retry_dispatch_scope_invalidation_pending";
            return false;
        }

        RuntimeUnlockRetryLifecycleAttemptSnapshot? effectiveRetryAttempt =
            retryLifecycle.InstalledAttempt is
            {
                State:
                    RuntimeUnlockRetryLifecycleAttemptState.Installed or
                    RuntimeUnlockRetryLifecycleAttemptState.AliasBound or
                    RuntimeUnlockRetryLifecycleAttemptState.SendInFlight,
            } activeAttempt
                ? activeAttempt
                : retryLifecycle.PendingAttempt is null &&
                  retryLifecycle.InstalledAttempt is null &&
                  retryLifecycle.RetainedPredecessor is null &&
                  retryLifecycle.BoundedClosure is null &&
                  retryLifecycle.LastTerminalAttempt is
             {
                 State:
                     RuntimeUnlockRetryLifecycleAttemptState.Settled,
                 Settlement.Outcome:
                     RuntimeUnlockRetryDispatchSettlementOutcome
                         .DispatchObserved,
             } dispatchObserved
                ? dispatchObserved
                : TryGetExactlyInvalidatedRuntimeUnlockRetryAttempt(
                    retryLifecycle,
                    installReceipt);
        var fallbackTail = state.PersistentState.FallbackTailYield;
        var tailYield = pairedRelease.LocalProof.Decision;
        if (activation is null ||
            activation.EffectiveRetryInstallReceipt != installReceipt ||
            activation.FallbackTailPairedReleaseReceipt != pairedRelease)
        {
            rejectionReason =
                "filetransfer_route_transition_fallback_activation_boundary_mismatch";
            return false;
        }

        if (fallbackTail.CurrentOperation is not
            {
                Decision: var currentYield,
                PairedRelease: { } currentRelease,
                PairingState:
                    FileTransferFallbackTailYieldPairingState.ReleaseConsumed,
            } ||
            currentYield != tailYield ||
            currentRelease != pairedRelease ||
            fallbackTail.LastConsumedReservation is not { } consumedRelease ||
            consumedRelease.Release != pairedRelease)
        {
            rejectionReason =
                "filetransfer_route_transition_fallback_paired_release_not_consumed";
            return false;
        }

        if (!IsEffectiveRuntimeUnlockRetryDispatchInstallReceipt(installReceipt))
        {
            rejectionReason =
                "filetransfer_route_transition_fallback_install_receipt_ineffective";
            return false;
        }

        if (effectiveRetryAttempt is not
            {
                InstallReceipt: { } currentInstallReceipt,
            } installedRetry ||
            installedRetry.Authority != installReceipt.Authority ||
            currentInstallReceipt != installReceipt)
        {
            rejectionReason =
                "filetransfer_route_transition_fallback_retry_attempt_not_current";
            return false;
        }

        if (fallbackTail.LastConsumingInstallReceipt is not
                { IsDefault: false } consumingInstallReceipt ||
            !IsRuntimeUnlockRetryInstallDescendedFromFallbackRelease(
                retryLifecycle,
                installedRetry,
                consumedRelease,
                consumingInstallReceipt))
        {
            rejectionReason =
                "filetransfer_route_transition_fallback_retry_lineage_mismatch";
            return false;
        }

        var retryBindingReason = "accepted";
        if (
            retryLifecycle.LastAuthorityGeneration !=
                installReceipt.Authority.AuthorityGeneration ||
            installReceipt.Request.IsDefault ||
            !TryValidateRuntimeUnlockRetryDispatchBinding(
                installReceipt.Authority,
                installReceipt.Request,
                out retryBindingReason))
        {
            rejectionReason = string.Equals(
                retryBindingReason,
                "accepted",
                StringComparison.Ordinal)
                ? "filetransfer_route_transition_fallback_retry_binding_mismatch"
                : retryBindingReason;
            return false;
        }

        if (
            installReceipt.Authority.RuntimeOwner != state.CurrentRuntimeOwner ||
            installReceipt.Authority.SourcePublicationAuthority !=
                state.CurrentRoutePublicationToken ||
            installReceipt.Authority.Route != state.RouteSelection.Route ||
            !string.Equals(
                installReceipt.Authority.RouteToken,
                state.RouteSelection.TelemetryToken,
                StringComparison.Ordinal) ||
            installReceipt.Authority.ProtocolVersion !=
                state.RouteSelection.ProtocolVersion ||
            installReceipt.Authority.LiveRouteEpoch !=
                (state.CurrentLiveRouteEpoch?.EpochId ?? 0) ||
            installReceipt.Authority.TerminalGeneration !=
                state.TerminalGeneration ||
            state.CurrentLeg is not { } currentLeg ||
            installReceipt.Authority.LegGeneration != currentLeg.Generation ||
            installReceipt.Authority.TransportEpoch !=
                currentLeg.TransportEpochId ||
            installReceipt.Authority.BridgeRecoveryGeneration !=
                currentLeg.BridgeRecoveryGeneration)
        {
            rejectionReason =
                "filetransfer_route_transition_fallback_retry_scope_mismatch";
            return false;
        }

        if (
            installReceipt.Authority.ReleaseKind !=
                RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield ||
            installReceipt.Authority.ReleaseAuthorityId != tailYield.AuthorityId ||
            installReceipt.Authority.ReleaseGeneration !=
                pairedRelease.ReleaseGeneration ||
            !string.Equals(
                installReceipt.Authority.ReleaseOutcome,
                pairedRelease.ReleaseOutcome,
                StringComparison.Ordinal))
        {
            rejectionReason =
                "filetransfer_route_transition_fallback_release_tuple_mismatch";
            return false;
        }

        if (
            !IsFallbackTailYieldScopeCurrent(
                state,
                GetEffectiveFallbackTailYieldScope(
                    fallbackTail.CurrentOperation.Value),
                out var fallbackScopeReason))
        {
            rejectionReason = fallbackScopeReason;
            return false;
        }

        var admissionReason = "accepted";
        var pendingReason = "accepted";
        if (
            state.CurrentRuntimeUnlockAdmission !=
                activation.RuntimeUnlockAdmission ||
            state.CurrentRuntimeUnlockPendingPreCommitProbe !=
                activation.PendingPreCommitProbe ||
            !IsRuntimeUnlockAdmissionTokenCurrent(
                state,
                activation.RuntimeUnlockAdmission,
                out admissionReason) ||
            !IsRuntimeUnlockPendingPreCommitProbeRequestCurrent(
                state,
                activation.PendingPreCommitProbe,
                out pendingReason))
        {
            rejectionReason = !string.Equals(
                admissionReason,
                "accepted",
                StringComparison.Ordinal)
                ? admissionReason
                : !string.Equals(
                    pendingReason,
                    "accepted",
                    StringComparison.Ordinal)
                    ? pendingReason
                    : "filetransfer_route_transition_fallback_precommit_authority_mismatch";
            return false;
        }

        var commitProofReason = "accepted";
        if (
            activation.PendingPreCommitProbe.AdmissionToken !=
                activation.RuntimeUnlockAdmission ||
            activation.PendingPreCommitProbe.TransactionGeneration !=
                activation.AcceptedLaunchBoundary.TransactionGeneration ||
            activation.PendingPreCommitProbe.OfferGeneration !=
                activation.AcceptedLaunchBoundary.OfferGeneration ||
            activation.PendingPreCommitProbe.TunaPathLeaseGeneration !=
                activation.AcceptedLaunchBoundary.TunaPathLeaseGeneration ||
            !string.Equals(
                activation.PendingPreCommitProbe.TunaPathLeaseListenerRunId,
                activation.AcceptedLaunchBoundary.TunaPathLeaseListenerRunId,
                StringComparison.Ordinal) ||
            activation.LaunchGeneration <= 0 ||
            !RuntimeUnlockTransaction.CanCommitRoute(
                activation.AcceptedLaunchBoundary,
                state,
                out commitProofReason))
        {
            rejectionReason = string.Equals(
                commitProofReason,
                "accepted",
                StringComparison.Ordinal)
                ? "filetransfer_route_transition_fallback_launch_boundary_mismatch"
                : commitProofReason;
            return false;
        }

        return true;
    }

    private static bool
        IsRuntimeUnlockRetryInstallDescendedFromFallbackRelease(
            RuntimeUnlockRetryLifecycleSubstate lifecycle,
            RuntimeUnlockRetryLifecycleAttemptSnapshot effectiveAttempt,
            FileTransferFallbackTailPairedReleaseReservation reservation,
            RuntimeUnlockRetryDispatchInstallReceipt consumingInstallReceipt)
    {
        if (reservation.IsDefault ||
            consumingInstallReceipt.IsDefault ||
            consumingInstallReceipt.Authority !=
                reservation.RetryAuthority)
        {
            return false;
        }

        static bool HasExactRelease(
            RuntimeUnlockRetryDispatchAuthority authority,
            FileTransferFallbackTailPairedReleaseReceipt release)
            => authority.ReleaseKind ==
                   RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield &&
               authority.ReleaseAuthorityId ==
                   release.ReleaseAuthorityId &&
               authority.ReleaseGeneration ==
                   release.ReleaseGeneration &&
               string.Equals(
                   authority.ReleaseOutcome,
                   release.ReleaseOutcome,
                   StringComparison.Ordinal);

        static bool TryFindAttempt(
            RuntimeUnlockRetryLifecycleSubstate source,
            FileTransferAuthorityId authorityId,
            out RuntimeUnlockRetryLifecycleAttemptSnapshot attempt)
        {
            foreach (var candidate in new[]
                     {
                         source.PendingAttempt,
                         source.InstalledAttempt,
                         source.RetainedPredecessor,
                         source.LastTerminalAttempt,
                     })
            {
                if (candidate is { } exact &&
                    exact.Authority.AuthorityId == authorityId)
                {
                    attempt = exact;
                    return true;
                }
            }

            var history = source.AttemptHistory.IsDefault
                ? ImmutableArray<RuntimeUnlockRetryLifecycleAttemptSnapshot>
                    .Empty
                : source.AttemptHistory;
            foreach (var historical in history)
            {
                if (historical.Authority.AuthorityId == authorityId)
                {
                    attempt = historical;
                    return true;
                }
            }

            attempt = default;
            return false;
        }

        var release = reservation.Release;
        var current = effectiveAttempt;
        var visited =
            new HashSet<FileTransferAuthorityId>();
        while (visited.Add(current.Authority.AuthorityId))
        {
            if (!HasExactRelease(current.Authority, release) ||
                current.InstallReceipt is not { IsDefault: false })
            {
                return false;
            }

            if (current.Authority.AuthorityId ==
                reservation.RetryAuthority.AuthorityId)
            {
                return current.Authority ==
                           reservation.RetryAuthority &&
                       current.InstallReceipt ==
                           consumingInstallReceipt;
            }

            if (current.Authority.PredecessorAuthorityId is not
                { IsDefault: false } predecessorAuthorityId ||
                current.Request.PredecessorAuthorityId !=
                    predecessorAuthorityId ||
                !TryFindAttempt(
                    lifecycle,
                    predecessorAuthorityId,
                    out var predecessor) ||
                !HasExactRelease(predecessor.Authority, release) ||
                current.Authority.DispatchAttemptGeneration !=
                    predecessor.Authority.DispatchAttemptGeneration + 1 ||
                current.Request.DispatchAttemptGeneration !=
                    current.Authority.DispatchAttemptGeneration)
            {
                return false;
            }

            if (current.Request.RequestActivePredecessorSupersession)
            {
                if (current.Authority.MissingDispatchSuccessorGeneration !=
                        predecessor.Authority
                            .MissingDispatchSuccessorGeneration ||
                    predecessor.Settlement is not
                    {
                        Outcome:
                            RuntimeUnlockRetryDispatchSettlementOutcome
                                .Superseded,
                    } superseded ||
                    superseded.SuccessorAuthorityId !=
                        current.Authority.AuthorityId)
                {
                    return false;
                }
            }
            else
            {
                var retiredOfferGeneration =
                    predecessor.OfferGeneration > 0
                        ? predecessor.OfferGeneration
                        : predecessor.Authority.RetiredOfferGeneration;
                if (current.Authority
                        .MissingDispatchSuccessorGeneration !=
                    predecessor.Authority
                        .MissingDispatchSuccessorGeneration + 1 ||
                    current.Request.RetiredOfferGeneration !=
                        retiredOfferGeneration ||
                    predecessor.Settlement is not
                    {
                        Outcome:
                            RuntimeUnlockRetryDispatchSettlementOutcome
                                .MissingDispatch,
                    })
                {
                    return false;
                }
            }

            current = predecessor;
        }

        return false;
    }

    private static RuntimeUnlockRetryLifecycleAttemptSnapshot?
        TryGetExactlyInvalidatedRuntimeUnlockRetryAttempt(
            RuntimeUnlockRetryLifecycleSubstate lifecycle,
            RuntimeUnlockRetryDispatchInstallReceipt installReceipt)
    {
        if (lifecycle.PendingAttempt is not null ||
            lifecycle.InstalledAttempt is not null ||
            lifecycle.RetainedPredecessor is not null ||
            lifecycle.BoundedClosure is not null ||
            lifecycle.PendingScopeInvalidationCommand is not null ||
            lifecycle.LastScopeInvalidationReceipt is not
            {
                Accepted: true,
                Command:
                {
                    Kind:
                        RuntimeUnlockRetryDispatchScopeInvalidationKind
                            .FullScope,
                    InstalledAttempt: { } invalidatedSource,
                },
            } ||
            invalidatedSource.InstallReceipt != installReceipt ||
            lifecycle.LastTerminalAttempt is not { } terminal)
        {
            return null;
        }

        var expectedTerminal = invalidatedSource.IsTerminal
            ? invalidatedSource
            : invalidatedSource with
            {
                State = RuntimeUnlockRetryLifecycleAttemptState.Invalidated,
            };
        return terminal == expectedTerminal
            ? terminal
            : null;
    }

    private static bool IsEffectiveRuntimeUnlockRetryDispatchInstallReceipt(
        RuntimeUnlockRetryDispatchInstallReceipt receipt)
        => !receipt.IsDefault &&
           !receipt.Request.IsDefault &&
           !receipt.StageCleared &&
           receipt.DispatchDeadlineUtcMs > 0 &&
           receipt.StageStatus is
               RuntimeUnlockRetryDispatchDecisionStageStatus.Staged or
               RuntimeUnlockRetryDispatchDecisionStageStatus.Idempotent &&
           (
               receipt.StageDisposition ==
                   RuntimeUnlockRetryDispatchStageDisposition.Retained &&
               receipt.StagedUtcMs > 0 &&
               receipt.StageExpiresUtcMs > receipt.StagedUtcMs ||
               receipt.StageStatus ==
                   RuntimeUnlockRetryDispatchDecisionStageStatus.Idempotent &&
               receipt.StageDisposition ==
                   RuntimeUnlockRetryDispatchStageDisposition.AlreadyInstalled &&
               receipt.StagedUtcMs == 0 &&
               receipt.StageExpiresUtcMs == 0
           );

    private static bool IsCommittedFileTransferRouteTransitionTerminalEvidenceValid(
        FileTransferRouteTransitionOperation terminal)
    {
        if (terminal.State != FileTransferRouteTransitionState.Committed ||
            terminal.DrainCommand is null ||
            terminal.DrainReceipt is null ||
            terminal.RoutePublicationToken is not { } publication ||
            terminal.DispositionCommand is not { } disposition ||
            disposition.Authority != terminal.Authority ||
            disposition.RouteCommitToken != terminal.RouteCommitToken ||
            disposition.RoutePublicationToken != publication ||
            disposition.FinalProof != terminal.FinalProof ||
            !IsFileTransferRouteTransitionDispositionCommandValid(disposition))
        {
            return false;
        }

        if (terminal.DispositionReceipt is { } dispositionReceipt &&
            (dispositionReceipt.Command != disposition ||
             dispositionReceipt.Status is not (
                 FileTransferRouteTransitionDispositionApplyStatus.Applied or
                 FileTransferRouteTransitionDispositionApplyStatus.Idempotent) ||
             !string.Equals(
                 dispositionReceipt.Reason,
                 dispositionReceipt.Status ==
                     FileTransferRouteTransitionDispositionApplyStatus.Idempotent
                     ? "filetransfer_route_transition_disposition_idempotent"
                     : "filetransfer_route_transition_committed",
                 StringComparison.Ordinal)))
        {
            return false;
        }

        if (terminal.Authority.TargetRoute == FileTransferRoute.FileTunaV4)
        {
            return terminal.RetainedFrameApplyCommand is null &&
                   terminal.RetainedFrameApplyReceipt is null;
        }

        if (terminal.Authority.ProofBoundary is not
                FileTransferFallbackTransitionProofBoundary fallback ||
            terminal.RetainedFrameApplyCommand is not { } frameCommand ||
            frameCommand != new FileTransferRouteTransitionRetainedFrameApplyCommand(
                terminal.Authority,
                publication.RuntimeOwner,
                publication,
                fallback.RetainedFrameIdentity) ||
            !IsFileTransferRouteTransitionRetainedFrameApplyCommandValid(
                frameCommand))
        {
            return false;
        }

        return terminal.RetainedFrameApplyReceipt is not { } frameReceipt ||
               frameReceipt.Command == frameCommand &&
               frameReceipt.Status is (
                   FileTransferRouteTransitionRetainedFrameApplyStatus.Applied or
                   FileTransferRouteTransitionRetainedFrameApplyStatus.Idempotent) &&
               string.Equals(
                   frameReceipt.Reason,
                   frameReceipt.Status ==
                       FileTransferRouteTransitionRetainedFrameApplyStatus.Applied
                       ? "filetransfer_route_transition_retained_frame_applied"
                       : "filetransfer_route_transition_retained_frame_apply_idempotent",
                   StringComparison.Ordinal);
    }

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
        else if (authority.SourceRouteCommit is null &&
                 state.CurrentRouteCommit is not null)
        {
            rejectionReason =
                "filetransfer_route_transition_unexpected_source_commit";
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

    private static bool TryValidateFileTransferRouteTransitionDrainReceipt(
        FileTransferCoordinatorState state,
        FileTransferRouteTransitionAuthority authority,
        FileTransferRouteTransitionPhysicalDrainReceipt receipt,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        var operation =
            state.PersistentState.FileTransferRouteTransition.CurrentOperation;
        if (operation is not
            {
                State: FileTransferRouteTransitionState.Draining,
                DrainCommand: { } command,
            } ||
            operation.Value.Authority != authority ||
            !IsFileTransferRouteTransitionSourceScopeCurrent(
                state,
                authority,
                out rejectionReason))
        {
            if (string.Equals(rejectionReason, "accepted", StringComparison.Ordinal))
            {
                rejectionReason = "filetransfer_route_transition_not_draining";
            }

            return false;
        }

        if (receipt.Command != command ||
            receipt.Outcome != FileTransferRouteTransitionPhysicalDrainOutcome.Completed ||
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
            rejectionReason = "filetransfer_route_transition_drain_receipt_mismatch";
            return false;
        }

        return true;
    }

    internal static bool TryValidateFileTransferRouteTransitionCommitProof(
        FileTransferCoordinatorState state,
        FileTransferRouteTransitionAuthority authority,
        RuntimeUnlockRouteCommitProof? finalProof,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        if (authority.TargetRoute == FileTransferRoute.PostTunaFallbackV6)
        {
            if (finalProof is not null ||
                authority.ProofBoundary is not
                    FileTransferFallbackTransitionProofBoundary fallback ||
                fallback.IsDefault ||
                fallback.PeerProofEvidence.FrameIdentity !=
                    fallback.RetainedFrameIdentity ||
                fallback.PeerProofEvidence.TransportEpoch !=
                    fallback.TargetTransportEpoch)
            {
                rejectionReason =
                    "filetransfer_route_transition_fallback_proof_mismatch";
                return false;
            }

            if (authority.Kind ==
                FileTransferRouteTransitionKind.TunaV4ToPostTunaFallbackV6)
            {
                if (fallback.SourceRouteCommit is not { } sourceCommit ||
                    authority.SourceRouteCommit != sourceCommit ||
                    state.CurrentRouteCommit != sourceCommit ||
                    !IsRouteCommitTokenCurrent(state, sourceCommit, out rejectionReason))
                {
                    if (string.Equals(
                            rejectionReason,
                            "accepted",
                            StringComparison.Ordinal))
                    {
                        rejectionReason =
                            "filetransfer_route_transition_tuna_commit_not_current";
                    }

                    return false;
                }
            }
            else if (authority.Kind ==
                     FileTransferRouteTransitionKind
                         .UncommittedTunaToPostTunaFallbackV6)
            {
                if (fallback.SourceRouteCommit is not null ||
                    state.CurrentRouteCommit is not null ||
                    authority.PredecessorTransitionAuthorityId is not
                        { IsDefault: false } predecessor ||
                    fallback.PredecessorTransitionAuthorityId != predecessor ||
                    state.PersistentState.FileTransferRouteTransition
                            .LastTerminalOperation is not
                        {
                            State: FileTransferRouteTransitionState.Invalidated,
                            Authority.AuthorityId: var retiredAuthority,
                            DispositionCommand.Kind:
                                FileTransferRouteTransitionDispositionKind.Superseded,
                        } ||
                    retiredAuthority != predecessor)
                {
                    rejectionReason =
                        "filetransfer_route_transition_predecessor_not_superseded";
                    return false;
                }
            }

            return true;
        }

        if (authority.TargetRoute != FileTransferRoute.FileTunaV4 ||
            finalProof is not { } proof ||
            proof.PathProbeState != RuntimeUnlockPathProbeState.Acked ||
            authority.ProofBoundary is not
                FileTransferTunaActivationTransitionProofBoundary activation ||
            activation.IsDefault ||
            proof.AdmissionToken != activation.RuntimeUnlockAdmission ||
            proof.TransactionGeneration !=
                activation.AcceptedLaunchBoundary.TransactionGeneration ||
            proof.OfferGeneration !=
                activation.AcceptedLaunchBoundary.OfferGeneration ||
            proof.TunaPathLeaseGeneration !=
                activation.AcceptedLaunchBoundary.TunaPathLeaseGeneration ||
            !string.Equals(
                proof.TunaPathLeaseListenerRunId,
                activation.AcceptedLaunchBoundary.TunaPathLeaseListenerRunId,
                StringComparison.Ordinal) ||
            !FileTransferTunaActivationProofMatchesTarget(proof) ||
            state.CurrentRuntimeUnlockAdmission != activation.RuntimeUnlockAdmission ||
            state.CurrentRuntimeUnlockPendingPreCommitProbe !=
                activation.PendingPreCommitProbe ||
            !IsRuntimeUnlockAdmissionTokenCurrent(
                state,
                activation.RuntimeUnlockAdmission,
                out rejectionReason) ||
            !IsRuntimeUnlockPendingPreCommitProbeRequestCurrent(
                state,
                activation.PendingPreCommitProbe,
                out rejectionReason))
        {
            if (string.Equals(rejectionReason, "accepted", StringComparison.Ordinal))
            {
                rejectionReason =
                    "filetransfer_route_transition_activation_proof_mismatch";
            }

            return false;
        }

        if (authority.Kind ==
            FileTransferRouteTransitionKind.PostTunaFallbackV6ToTunaV4)
        {
            var activationRejectionReason = "accepted";
            if (activation.EffectiveRetryInstallReceipt is not
                    { IsDefault: false } installReceipt ||
                activation.FallbackTailPairedReleaseReceipt is not
                    { IsDefault: false } pairedRelease ||
                proof != activation.AcceptedLaunchBoundary ||
                !TryValidateFileTransferFallbackTunaActivationBoundaryCurrent(
                    state,
                    installReceipt,
                    pairedRelease,
                    activation,
                    out activationRejectionReason))
            {
                rejectionReason = string.Equals(
                    activationRejectionReason,
                    "accepted",
                    StringComparison.Ordinal)
                    ? "filetransfer_route_transition_fallback_release_not_current"
                    : activationRejectionReason;
                return false;
            }
        }

        return true;
    }

    private static bool FileTransferTunaActivationProofMatchesTarget(
        RuntimeUnlockRouteCommitProof proof)
        => proof.TargetRoute == FileTransferRoute.FileTunaV4 &&
           proof.ProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
           proof.TargetTransport == FileTransferTransportKind.Tuna &&
           proof.HandoffKind ==
               FileTransferTransportHandoffKind.NormalToTunaActivation &&
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
           proof.TunaPathLeaseState ==
               RuntimeUnlockTunaPathLeaseState.ListenerReady &&
           proof.TunaPathLeaseCurrent;

    private static bool
        IsExactOrCanonicalIdempotentFileTransferRouteTransitionDrainReceipt(
            FileTransferRouteTransitionPhysicalDrainReceipt committed,
            FileTransferRouteTransitionPhysicalDrainReceipt candidate)
        => committed.Command == candidate.Command &&
           candidate.Outcome ==
               FileTransferRouteTransitionPhysicalDrainOutcome.Completed &&
           committed.CompletedPhysicalSendCount ==
               candidate.CompletedPhysicalSendCount &&
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

    private static FileTransferRouteTransitionPhysicalDrainReceipt
        SnapshotFileTransferRouteTransitionDrainReceipt(
            FileTransferRouteTransitionPhysicalDrainReceipt receipt)
        => receipt with
        {
            CompletedPhysicalSendLeaseIds = Array.AsReadOnly(
                receipt.CompletedPhysicalSendLeaseIds.ToArray()),
            Reason = NormalizeReason(receipt.Reason),
        };

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
                    receipt.Command.PredecessorAuthority is null
                        ? "filetransfer_route_transition_pause_bound"
                        : "filetransfer_route_transition_pause_rebound",
                    StringComparison.Ordinal) &&
                (receipt.Command.PredecessorAuthority is null ||
                 receipt.Command.PredecessorAuthority.Value.IsDefault is false),
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
            FileTransferRouteTransitionRetainedFrameApplyCommand?
                retainedFrameApplyCommand = null,
            FileTransferCoordinatorDecision? routeDecision = null)
        => new(
            status,
            NormalizeReason(reason),
            authority,
            pauseBindCommand,
            drainCommand,
            dispositionCommand,
            retainedFrameApplyCommand,
            routeDecision)
        {
            State = state,
        };

    private static string NormalizeReason(string? reason)
        => string.IsNullOrWhiteSpace(reason)
            ? "file_transfer_coordinator"
            : reason.Trim();
}
