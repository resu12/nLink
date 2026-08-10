namespace NLink.Core.FileTransfer;

public sealed record FileTransferChunkBudgetRequest(
    string TransferId,
    long FileSizeBytes,
    int RequestedChunkSizeBytes,
    int NegotiatedDataProtocolVersion);

public interface IFileTransferChunkBudgetProvider
{
    int ResolveSafeOutboundChunkSize(FileTransferChunkBudgetRequest request);
}

public enum FileTransferTransportProfileKind
{
    Default = 0,
    ConservativeNknStartup = 1,
}

internal enum FileTransferDataSessionSubmissionUnavailableReason
{
    SubmissionEpochNotReady = 0,
    SubmissionEpochChanged = 1,
    ProcessEpochNotReady = 2,
    ProcessEpochChanged = 3,
}

internal sealed class FileTransferDataSessionSubmissionUnavailableException : InvalidOperationException
{
    internal FileTransferDataSessionSubmissionUnavailableException(
        FileTransferDataSessionSubmissionUnavailableReason reason,
        string stage,
        string channel,
        long expectedSubmissionEpoch,
        long currentSubmissionEpoch,
        long expectedProcessEpoch,
        long currentProcessEpoch,
        bool recoveryBarrierActive,
        string compatibilityMessage)
        : base(compatibilityMessage)
    {
        Reason = reason;
        Stage = stage;
        Channel = channel;
        ExpectedSubmissionEpoch = expectedSubmissionEpoch;
        CurrentSubmissionEpoch = currentSubmissionEpoch;
        ExpectedProcessEpoch = expectedProcessEpoch;
        CurrentProcessEpoch = currentProcessEpoch;
        RecoveryBarrierActive = recoveryBarrierActive;
    }

    internal FileTransferDataSessionSubmissionUnavailableReason Reason { get; }
    internal string Stage { get; }
    internal string Channel { get; }
    internal long ExpectedSubmissionEpoch { get; }
    internal long CurrentSubmissionEpoch { get; }
    internal long ExpectedProcessEpoch { get; }
    internal long CurrentProcessEpoch { get; }
    internal bool RecoveryBarrierActive { get; }
}

public interface IFileTransferTransportProfileProvider
{
    FileTransferTransportProfileKind FileTransferTransportProfileKind { get; }
}

public interface IFileTransferProtocolCapabilities
{
    bool SupportsFileTransferV6Streaming { get; }
}

public sealed record FileTransferBridgeRecoveryAuthority(
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    string RouteToken,
    int ProtocolVersion,
    int LiveRouteEpoch,
    int RuntimeGeneration,
    int TerminalGeneration,
    int TransferLegGeneration,
    long TransportEpoch,
    int BridgeRecoveryGeneration,
    int BridgeDecisionGeneration,
    string RecoveryKind,
    string Reason);

public sealed record FileTransferRecoveryLivenessDeferralGrant(
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    string RouteToken,
    int ProtocolVersion,
    int LiveRouteEpoch,
    int RuntimeGeneration,
    int TerminalGeneration,
    int TransferLegGeneration,
    long TransportEpoch,
    int BridgeRecoveryGeneration,
    int LeaseGeneration,
    string LeaseState,
    string Reason,
    DateTimeOffset LivenessDeferralDeadlineUtc);

public sealed record FileTransferReceiveRecoveryRequest(
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    string Reason)
{
    internal FileTransferReceiveRecoveryObservationSnapshot? Observation { get; init; }

    internal FileTransferBridgeRecoveryEvaluationRequest?
        HealthEvaluationRequest { get; init; }

    public string? RouteToken { get; init; }

    public int ProtocolVersion { get; init; }

    public int LiveRouteEpoch { get; init; }

    public int TransferLegGeneration { get; init; }

    public int BridgeRecoveryGeneration { get; init; }

    public long TransportEpoch { get; init; }

    public string? CheckpointRequestId { get; init; }

    public string? AuthorityReason { get; init; }

    public FileTransferBridgeRecoveryAuthority? BridgeRecoveryAuthority { get; init; }

    internal FileTransferBridgeRecoveryExecutionCommand? BridgeRecoveryExecutionCommand { get; init; }

    internal FileTransferReceiveRecoveryOperationAuthority?
        ReceiveRecoveryOperationAuthority { get; init; }
}

public interface IFileTransferReceiveRecoveryController
{
    void RequestFileTransferReceiveRecovery(FileTransferReceiveRecoveryRequest request);
}

internal enum FileTransferBridgeRecoveryExecutionStartStatus
{
    Started,
    Idempotent,
    Rejected,
}

internal enum FileTransferBridgeRecoveryExecutionOutcome
{
    Completed,
    Exhausted,
    Discarded,
}

internal enum FileTransferBridgeRecoveryExecutionState
{
    Issued,
    Running,
}

internal readonly record struct FileTransferBridgeRecoveryExecutionCommand(
    BridgeRecoveryDecision Decision,
    RecoveryLivenessLease? RecoveryLivenessLease,
    string? CheckpointRequestId)
{
    public bool IsDefault =>
        Decision.IsDefault ||
        !Decision.Accepted ||
        Decision.RecoveryKind == BridgeRecoveryKind.Unknown;
}

internal readonly record struct FileTransferBridgeRecoveryExecutionReceipt(
    FileTransferBridgeRecoveryExecutionCommand Command,
    FileTransferBridgeRecoveryExecutionOutcome Outcome,
    string ExecutionMode,
    string Reason)
{
    public bool IsDefault =>
        Command.IsDefault ||
        string.IsNullOrWhiteSpace(ExecutionMode) ||
        string.IsNullOrWhiteSpace(Reason) ||
        !string.Equals(ExecutionMode, ExecutionMode.Trim(), StringComparison.Ordinal) ||
        !string.Equals(Reason, Reason.Trim(), StringComparison.Ordinal);
}

internal readonly record struct FileTransferBridgeRecoveryExecutionStartResult(
    FileTransferBridgeRecoveryExecutionCommand Command,
    FileTransferBridgeRecoveryExecutionStartStatus Status,
    string Reason,
    Task<FileTransferBridgeRecoveryExecutionReceipt>? Completion)
{
    public bool Accepted =>
        !Command.IsDefault &&
        !string.IsNullOrWhiteSpace(Reason) &&
        string.Equals(Reason, Reason.Trim(), StringComparison.Ordinal) &&
        Status is (
            FileTransferBridgeRecoveryExecutionStartStatus.Started or
            FileTransferBridgeRecoveryExecutionStartStatus.Idempotent) &&
        Completion is not null;
}

internal enum FileTransferBridgeRecoveryEvaluationKind
{
    Unknown = 0,
    ReceiveStall = 1,
    PingTimeout = 2,
    Diagnostic = 3,
    SessionLivenessTimeout = 4,
}

internal readonly record struct FileTransferBridgeRecoveryEvaluationRequest(
    string? SessionId,
    string? TransferId,
    FileTransferDirection? Direction,
    FileTransferBridgeRecoveryEvaluationKind EvaluationKind,
    string ObservationKind,
    string Reason,
    long ObservationGeneration,
    long ObservedUtcMs,
    int ConsecutiveObservations = 0,
    long FramesSentSinceLast = 0,
    long MessagesReceivedSinceLast = 0,
    long ControlLastReceivedAgeMs = -1,
    long BulkLastReceivedAgeMs = -1)
{
    public bool IsDefault =>
        EvaluationKind == FileTransferBridgeRecoveryEvaluationKind.Unknown ||
        string.IsNullOrWhiteSpace(ObservationKind) ||
        string.IsNullOrWhiteSpace(Reason) ||
        !string.Equals(ObservationKind, ObservationKind.Trim(), StringComparison.Ordinal) ||
        !string.Equals(Reason, Reason.Trim(), StringComparison.Ordinal) ||
        ObservationGeneration <= 0 ||
        ObservedUtcMs <= 0;
}

internal sealed class FileTransferBridgeRecoveryEvaluationRequestedEventArgs(
    FileTransferBridgeRecoveryEvaluationRequest request) : EventArgs
{
    public FileTransferBridgeRecoveryEvaluationRequest Request { get; } = request;
}

internal readonly record struct FileTransferReceiveRecoveryProofObservation(
    FileTransferBridgeRecoveryExecutionCommand Command,
    string ProofIdentity,
    string Reason,
    long ObservedUtcMs)
{
    public bool IsDefault =>
        Command.IsDefault ||
        string.IsNullOrWhiteSpace(ProofIdentity) ||
        string.IsNullOrWhiteSpace(Reason) ||
        ObservedUtcMs <= 0;
}

internal sealed class FileTransferReceiveRecoveryProofObservedEventArgs(
    FileTransferReceiveRecoveryProofObservation observation) : EventArgs
{
    public FileTransferReceiveRecoveryProofObservation Observation { get; } =
        observation;
}

internal sealed class
    FileTransferReceiveRecoveryPeerReachabilityObservedEventArgs(
        FileTransferReceiveRecoveryPeerReachabilityObservation observation)
    : EventArgs
{
    public FileTransferReceiveRecoveryPeerReachabilityObservation Observation
    {
        get;
    } = observation;
}

internal interface IFileTransferBridgeRecoveryExecutionBridge
{
    event EventHandler<FileTransferBridgeRecoveryEvaluationRequestedEventArgs>?
        BridgeRecoveryEvaluationRequested;

    event EventHandler<FileTransferReceiveRecoveryProofObservedEventArgs>?
        ReceiveRecoveryProofObserved;

    event EventHandler<
        FileTransferReceiveRecoveryPeerReachabilityObservedEventArgs>?
        ReceiveRecoveryPeerReachabilityObserved;

    FileTransferBridgeRecoveryExecutionStartResult BeginBridgeRecoveryExecution(
        FileTransferBridgeRecoveryExecutionCommand command);

    void ObserveBridgeRecoveryExecutionReceiptDecision(
        FileTransferBridgeRecoveryExecutionReceipt receipt,
        bool accepted,
        string reason);

    bool InvalidateBridgeRecoveryExecution(
        FileTransferBridgeRecoveryExecutionCommand command,
        string reason);
}

public enum FileTransferRecoveryLivenessState
{
    AuthorityActive = 0,
    BridgeRecoveryRequested = 1,
    BridgeRecoveryStarted = 2,
    BridgeRecoveryCompletedAwaitingProof = 3,
    ReceiveProofObserved = 4,
    Exhausted = 5,
    Completed = 6,
}

public sealed record FileTransferRecoveryLivenessSnapshot(
    string SessionId,
    string TransferId,
    string RouteToken,
    int ProtocolVersion,
    int LiveRouteEpoch,
    int TransferLegGeneration,
    int BridgeRecoveryGeneration,
    long TransportEpoch,
    string? CheckpointRequestId,
    string AuthorityReason,
    FileTransferRecoveryLivenessState State,
    DateTimeOffset CreatedUtc,
    DateTimeOffset LivenessDeferralDeadlineUtc,
    bool BridgeRecoveryRequested,
    bool BridgeRecoveryStarted,
    bool BridgeRecoveryCompleted,
    bool ReceiveProofObserved,
    bool RecoveryExhausted,
    bool AuthorityCompleted,
    bool TerminalRecommended);

public interface IFileTransferRecoveryLivenessState
{
    bool TryGetActiveFileTransferRecoveryLivenessSnapshot(
        string sessionId,
        out FileTransferRecoveryLivenessSnapshot snapshot);
}

public interface IFileTransferRecoveryLivenessAuthorityObserver
{
    void NotifyFileTransferRecoveryLivenessAuthorityRejected(
        FileTransferRecoveryLivenessSnapshot snapshot,
        string reason);
}

public sealed record FileTransferTerminalNotification(
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    string RouteToken,
    int ProtocolVersion,
    FileTransferTransferState TerminalState,
    string? ErrorCode,
    string? Reason);

public interface IFileTransferTerminalObserver
{
    void ObserveFileTransferTerminal(FileTransferTerminalNotification notification);
}

public sealed record FileTransferRegularV4FrontierRepairAuthority(
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    string RouteToken,
    int ProtocolVersion,
    int LiveRouteEpoch,
    int RuntimeGeneration,
    int TerminalGeneration,
    long LegGeneration,
    long TransportEpoch,
    int BridgeRecoveryGeneration,
    int BridgeDecisionGeneration,
    int RoutePublicationGeneration,
    int? RouteCommitGeneration,
    string RepairKey,
    int FrontierChunkIndex,
    int DurableReceivedHighestChunkIndex,
    int PendingRepairCount,
    int DecisionGeneration,
    string DecisionReason,
    FileTransferTransportKind TargetTransport = FileTransferTransportKind.Unknown)
{
    // Coordinator-issued runtime contracts always populate this value. It is
    // optional here to preserve source compatibility for diagnostic-only DTO
    // fixtures and older transport adapters.
    public FileTransferAuthorityId AuthorityId { get; init; }

    public FileTransferAuthorityId? PredecessorAuthorityId { get; init; }
}

public enum FileTransferRegularV4FrontierRepairSettlementOutcome
{
    ProgressObserved,
    MissingDispatch,
    SettledStalled,
    AuthorityRejected,
    TerminalRejected,
    Superseded,
    StaleRejected,
    TransferTerminal,
}

public sealed record FileTransferRegularV4FrontierRepairSettlement(
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    string RouteToken,
    int ProtocolVersion,
    int LiveRouteEpoch,
    int RuntimeGeneration,
    int TerminalGeneration,
    long LegGeneration,
    long TransportEpoch,
    int BridgeRecoveryGeneration,
    int BridgeDecisionGeneration,
    int RoutePublicationGeneration,
    int? RouteCommitGeneration,
    string RepairKey,
    int FrontierChunkIndex,
    int DurableReceivedHighestChunkIndex,
    int RequestedChunkCount,
    int DecisionGeneration,
    string DecisionReason,
    FileTransferRegularV4FrontierRepairSettlementOutcome Outcome,
    FileTransferTransportKind TargetTransport = FileTransferTransportKind.Unknown)
{
    public FileTransferAuthorityId AuthorityId { get; init; }

    public FileTransferAuthorityId? SuccessorAuthorityId { get; init; }
}

public sealed record FileTransferRegularV4ControlFeedbackPressure(
    string SessionId,
    string TransferId,
    long CreditExhaustedTimeMs,
    int FrontierLagChunks,
    int PendingRepairCount,
    string Reason,
    FileTransferRegularV4FrontierRepairAuthority? RepairAuthority = null);

public interface IFileTransferRegularV4ControlFeedbackPressureObserver
{
    void ObserveRegularV4ControlFeedbackPressure(FileTransferRegularV4ControlFeedbackPressure pressure);
}

internal enum FileTransferRegularV4FrontierRepairPressureInstallStatus
{
    Installed,
    Idempotent,
    Rejected,
}

internal sealed record FileTransferRegularV4FrontierRepairPressureInstallReceipt(
    FileTransferRegularV4FrontierRepairAuthority? Authority,
    FileTransferRegularV4FrontierRepairPressureInstallStatus Status,
    string Reason,
    long InstallGeneration,
    string PressureReason);

internal sealed record FileTransferRegularV4FrontierRepairPressureInstallCommand(
    FileTransferRegularV4ControlFeedbackPressure Pressure,
    long InstallGeneration);

internal enum FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus
{
    Applied,
    Idempotent,
    Rejected,
}

internal sealed record FileTransferRegularV4FrontierRepairPressureSettlementCommand(
    FileTransferRegularV4FrontierRepairSettlement Settlement,
    long InstallGeneration,
    FileTransferAuthorityId CurrentAuthorityId,
    FileTransferAuthorityId? RetainedPredecessorAuthorityId);

internal sealed record FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt(
    FileTransferRegularV4FrontierRepairPressureSettlementCommand Command,
    FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus Status,
    string Reason,
    bool CurrentAuthorityCleared,
    bool RetainedPredecessorCleared);

internal interface IFileTransferRegularV4FrontierRepairPressureInstallBridge
{
    FileTransferRegularV4FrontierRepairPressureInstallReceipt InstallRegularV4FrontierRepairPressure(
        FileTransferRegularV4FrontierRepairPressureInstallCommand command);

    FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt
        ApplyRegularV4FrontierRepairPressureSettlement(
            FileTransferRegularV4FrontierRepairPressureSettlementCommand command);
}

internal interface IFileTransferRouteTransitionBridge
{
    event EventHandler<FileTransferFallbackV6ProofBootstrapEvaluationRequestedEventArgs>?
        FallbackV6ProofBootstrapEvaluationRequested;

    FileTransferRouteTransitionPauseBindReceipt BindFileTransferRouteTransitionPause(
        FileTransferRouteTransitionPauseBindCommand command);

    FileTransferRouteTransitionDispositionReceipt
        ApplyFileTransferRouteTransitionDisposition(
            FileTransferRouteTransitionDispositionCommand command);

    Task<FileTransferFallbackV6ProofBootstrapReceipt>
        ExecuteFileTransferFallbackV6ProofBootstrapAsync(
            FileTransferFallbackV6ProofBootstrapCommand command,
            FileTransferTransportEpochFrameV6 epochFrame,
            FileTransferDataFrame proofFrame,
            IFileTransferDataSession dataSession,
            Func<bool> isCommandCurrent,
            CancellationToken ct);
}

internal sealed record FileTransferFallbackV6ProofBootstrapEvaluation(
    string SessionId,
    FileTransferDirection Direction,
    long ObservationGeneration,
    long FallbackEpoch,
    string Source,
    string ProofKind,
    long ObservedUtcMilliseconds);

internal sealed class FileTransferFallbackV6ProofBootstrapEvaluationRequestedEventArgs(
    FileTransferFallbackV6ProofBootstrapEvaluation evaluation) : EventArgs
{
    public FileTransferFallbackV6ProofBootstrapEvaluation Evaluation { get; } = evaluation;
}

public interface IFileTransferRegularV4FrontierRepairSettlementObserver
{
    void ObserveRegularV4FrontierRepairSettlement(FileTransferRegularV4FrontierRepairSettlement settlement);
}

public sealed record FileTransferPostTunaFallbackControlPlanePressure(
    string SessionId,
    string TransferId,
    string RouteToken,
    int ProtocolVersion,
    int LiveRouteEpoch,
    int TransferLegGeneration,
    int BridgeRecoveryGeneration,
    long TransportEpoch,
    string? CheckpointRequestId,
    string Kind,
    string Reason);

public interface IFileTransferPostTunaFallbackControlPlanePressureObserver
{
    void ObservePostTunaFallbackControlPlanePressure(FileTransferPostTunaFallbackControlPlanePressure pressure);

    void ClearPostTunaFallbackControlPlanePressure(string transferId, string reason);
}

public sealed record FileTransferRouteCompletedNotification(
    string SessionId,
    string TransferId,
    string RouteToken,
    int ProtocolVersion);

public interface IFileTransferRouteCompletionObserver
{
    void ObserveFileTransferRouteCompleted(FileTransferRouteCompletedNotification notification);
}

public static class FileTransferInitialTunaReadinessScopes
{
    public const string Transfer = "transfer";
    public const string SessionReactivation = "session_reactivation";
}

public sealed record FileTransferInitialTunaReadinessEvidence(
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    DateTimeOffset CapturedUtc,
    long LeaseGeneration,
    string? ListenerRunId,
    bool LeaseCurrent,
    bool ListenerReady,
    long OfferGeneration,
    bool PeerVisibleProof,
    bool AnswerReceived,
    bool AnswerAckReceived,
    bool TunaNegotiatedObserved,
    string ReadinessScope = FileTransferInitialTunaReadinessScopes.Transfer,
    string? SourceTransferId = null,
    long SnapshotGeneration = 0,
    DateTimeOffset SourceCapturedUtc = default);

public interface IFileTransferInitialTunaReadinessProvider
{
    bool TryGetInitialTunaReadinessEvidence(
        string sessionId,
        string transferId,
        FileTransferDirection direction,
        out FileTransferInitialTunaReadinessEvidence? evidence);
}

public sealed record FileTransferRoutePublicationAuthority(
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    string RouteToken,
    int ProtocolVersion,
    int LiveRouteEpoch,
    int RuntimeGeneration,
    int TerminalGeneration,
    long LegGeneration,
    int RoutePublicationGeneration,
    int? RouteCommitGeneration,
    string TargetRouteToken,
    int TargetProtocolVersion,
    FileTransferTransportKind TargetTransport,
    string RoutePublicationActionKind,
    string CoordinatorDecisionReason);

public sealed record FileTransferDataPathSendAuthority(
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    string RouteToken,
    int ProtocolVersion,
    int LiveRouteEpoch,
    int RuntimeGeneration,
    int TerminalGeneration,
    long LegGeneration,
    int RoutePublicationGeneration,
    int? RouteCommitGeneration,
    string TargetRouteToken,
    int TargetProtocolVersion,
    FileTransferTransportKind TargetTransport,
    string RoutePublicationActionKind,
    string CoordinatorDecisionReason);

public sealed class FileTransferRequiredTunaBulkUnavailableException : InvalidOperationException
{
    public FileTransferRequiredTunaBulkUnavailableException(string reason)
        : base("Required Tuna file-transfer bulk send was not observed.")
    {
        Reason = string.IsNullOrWhiteSpace(reason) ? "required_tuna_bulk_unavailable" : reason.Trim();
    }

    public string Reason { get; }
}

public sealed record FileTransferRouteHintNotification(
    string SessionId,
    string TransferId,
    string RouteToken,
    int ProtocolVersion,
    string Source,
    bool RuntimeUnlockAdmissionGranted = false,
    string RuntimeUnlockAdmissionReason = "unknown",
    string FallbackLegState = "unknown",
    bool FallbackLegCanSendData = false,
    long FallbackLegGeneration = 0,
    string? FallbackCheckpointRequestId = null,
    FileTransferRoutePublicationAuthority? PublicationAuthority = null,
    long FallbackTransportEpoch = 0,
    int FallbackBridgeRecoveryGeneration = 0);

public interface IFileTransferRouteHintObserver
{
    void ObserveFileTransferRouteHint(FileTransferRouteHintNotification notification);
}

public sealed record RuntimeUnlockPreCommitProbeLaunchHoldNotification(
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    long TransactionGeneration,
    long OfferGeneration,
    long TunaPathLeaseGeneration,
    string Source,
    string Reason,
    string AdmissionReason,
    int LiveRouteEpoch,
    int RuntimeGeneration,
    int TerminalGeneration,
    long LegGeneration);

public interface IRuntimeUnlockPreCommitProbeLaunchObserver
{
    void NotifyRuntimeUnlockPreCommitProbeLaunchHeld(RuntimeUnlockPreCommitProbeLaunchHoldNotification notification);
}

internal sealed record RuntimeUnlockRouteCommitSnapshot(
    string SessionId,
    string? TransferId,
    long TransactionGeneration,
    long OfferGeneration,
    bool PeerVisibleProof,
    bool PeerReceived,
    bool AnswerReceived,
    FileTransferRoute TargetRoute,
    int ProtocolVersion,
    FileTransferTransportHandoffKind HandoffKind,
    FileTransferTransportKind TargetTransport,
    string TransactionState,
    string Reason,
    string? PathProbeId = null,
    string PathProbeState = "none",
    FileTransferTransportKind PathProbeTransport = FileTransferTransportKind.Unknown,
    long PathProbeAckedUtcMs = 0,
    string? PathProbeFailureReason = null,
    bool TunaPathLeaseRequired = false,
    long TunaPathLeaseGeneration = 0,
    string TunaPathLeaseState = "none",
    string? TunaPathLeaseListenerRunId = null,
    bool TunaPathLeaseCurrent = false,
    string? TunaPathLeaseFailureReason = null,
    long LaunchGeneration = 0,
    RuntimeUnlockResponderPeerProbeRouteTransitionObservation?
        ResponderPeerProbeRouteTransitionObservation = null);

internal sealed record RuntimeUnlockResponderPeerProbeRouteTransitionObservation(
    string SessionId,
    string TransferId,
    long TransactionGeneration,
    long OfferGeneration,
    long PendingAnswerAckGeneration,
    long PayerDecisionId,
    long LaunchGeneration,
    string Lanes,
    string AnswerAcceptanceSource,
    string? AckMessageId,
    string? AckSourceFingerprint,
    string? PeerProbeFrameFingerprint,
    long TunaPathLeaseGeneration,
    string? TunaPathLeaseListenerRunId,
    string ProbeId,
    RuntimeUnlockPathProbeState ProbeState,
    FileTransferTransportKind ProbeTransport,
    long ProbeAckedUtcMs,
    FileTransferRoute TargetRoute,
    int TargetProtocolVersion,
    FileTransferTransportHandoffKind HandoffKind,
    FileTransferTransportKind TargetTransport,
    RuntimeUnlockPeerPreCommitAnswerAcceptanceScope PeerReceiveScope = default)
{
    public RuntimeUnlockResponderPeerProbeTransitionMode ResponderTransitionMode =>
        PeerReceiveScope.ResponderTransitionMode;

    public bool IsDefault =>
        string.IsNullOrWhiteSpace(SessionId) ||
        string.IsNullOrWhiteSpace(TransferId) ||
        TransactionGeneration <= 0 ||
        OfferGeneration <= 0 ||
        PendingAnswerAckGeneration <= 0 ||
        PayerDecisionId <= 0 ||
        LaunchGeneration <= 0 ||
        !string.Equals(Lanes, "file", StringComparison.Ordinal) ||
        !string.Equals(
            AnswerAcceptanceSource,
            "SemanticAnswerAck",
            StringComparison.Ordinal) ||
        string.IsNullOrWhiteSpace(AckMessageId) ||
        string.IsNullOrWhiteSpace(AckSourceFingerprint) ||
        string.IsNullOrWhiteSpace(PeerProbeFrameFingerprint) ||
        TunaPathLeaseGeneration <= 0 ||
        string.IsNullOrWhiteSpace(TunaPathLeaseListenerRunId) ||
        string.IsNullOrWhiteSpace(ProbeId) ||
        ProbeState != RuntimeUnlockPathProbeState.Acked ||
        ProbeTransport != FileTransferTransportKind.Tuna ||
        ProbeAckedUtcMs <= 0 ||
        TargetRoute != FileTransferRoute.FileTunaV4 ||
        TargetProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
        HandoffKind !=
            FileTransferTransportHandoffKind.NormalToTunaActivation ||
        TargetTransport != FileTransferTransportKind.Tuna ||
        PeerReceiveScope.IsDefault;
}

internal sealed record PostTunaFallbackHandoffEvidence(
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    long ProofGeneration,
    string SourceRouteToken,
    int SourceProtocolVersion,
    int SourceLiveRouteEpoch,
    int SourceLegGeneration,
    int SourceBridgeRecoveryGeneration,
    long SourceTransportEpoch,
    string CheckpointRequestId,
    string ProofKind,
    string Reason,
    PostTunaFallbackPeerProofFrameIdentity? FrameIdentity = null)
{
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(SessionId) &&
        !string.IsNullOrWhiteSpace(TransferId) &&
        (Direction is FileTransferDirection.Outbound or
            FileTransferDirection.Inbound) &&
        ProofGeneration > 0 &&
        string.Equals(
            SourceRouteToken,
            FileTransferRouteResolver.PostTunaFallbackV6Token,
            StringComparison.Ordinal) &&
        SourceProtocolVersion == FileTransferProtocol.ProtocolVersionV6 &&
        SourceLiveRouteEpoch > 0 &&
        SourceLegGeneration > 0 &&
        SourceBridgeRecoveryGeneration > 0 &&
        SourceTransportEpoch > 0 &&
        !string.IsNullOrWhiteSpace(CheckpointRequestId) &&
        !string.IsNullOrWhiteSpace(ProofKind) &&
        !string.IsNullOrWhiteSpace(Reason);

    public bool IsExactFrameBound =>
        FrameIdentity is { IsComplete: true } identity &&
        identity.TransportEpoch == SourceTransportEpoch;

    public bool CanBeReplacedBy(PostTunaFallbackHandoffEvidence candidate)
        => candidate.IsComplete &&
           candidate.ProofGeneration > ProofGeneration &&
           string.Equals(candidate.SessionId, SessionId, StringComparison.Ordinal) &&
           string.Equals(candidate.TransferId, TransferId, StringComparison.Ordinal) &&
           candidate.Direction == Direction &&
           string.Equals(candidate.SourceRouteToken, SourceRouteToken, StringComparison.Ordinal) &&
           candidate.SourceProtocolVersion == SourceProtocolVersion &&
           candidate.SourceLiveRouteEpoch == SourceLiveRouteEpoch &&
           candidate.SourceLegGeneration == SourceLegGeneration &&
           candidate.SourceBridgeRecoveryGeneration >= SourceBridgeRecoveryGeneration &&
           candidate.SourceTransportEpoch >= SourceTransportEpoch &&
           string.Equals(
               candidate.CheckpointRequestId,
               CheckpointRequestId,
               StringComparison.Ordinal) &&
           string.Equals(candidate.ProofKind, ProofKind, StringComparison.Ordinal);

    public PostTunaFallbackPeerProofEvidence ToPeerProofEvidence()
        => new(
            SessionId,
            TransferId,
            Direction,
            ProofGeneration,
            SourceTransportEpoch,
            ProofKind,
            Reason,
            FrameIdentity);
}

internal sealed record PostTunaFallbackPeerProofEvidence(
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    long ProofGeneration,
    long TransportEpoch,
    string ProofKind,
    string Reason,
    PostTunaFallbackPeerProofFrameIdentity? FrameIdentity)
{
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(SessionId) &&
        !string.IsNullOrWhiteSpace(TransferId) &&
        (Direction is FileTransferDirection.Outbound or
            FileTransferDirection.Inbound) &&
        ProofGeneration > 0 &&
        TransportEpoch > 0 &&
        !string.IsNullOrWhiteSpace(ProofKind) &&
        !string.IsNullOrWhiteSpace(Reason) &&
        FrameIdentity is { IsComplete: true } identity &&
        identity.TransportEpoch == TransportEpoch;

    public bool IsExactFrameBound => IsComplete;

    public bool CanBeReplacedBy(PostTunaFallbackPeerProofEvidence candidate)
        => candidate.IsComplete &&
           candidate.ProofGeneration > ProofGeneration &&
           string.Equals(candidate.SessionId, SessionId, StringComparison.Ordinal) &&
           string.Equals(candidate.TransferId, TransferId, StringComparison.Ordinal) &&
           candidate.Direction == Direction &&
           candidate.TransportEpoch >= TransportEpoch &&
           string.Equals(candidate.ProofKind, ProofKind, StringComparison.Ordinal);
}

internal sealed record PostTunaFallbackAvailabilityProofEvidence(
    PostTunaFallbackPeerProofEvidence PeerProofEvidence,
    RuntimeUnlockRouteCommitSnapshot PreCommitSnapshot)
{
    public bool IsComplete =>
        PeerProofEvidence.IsComplete &&
        PreCommitSnapshot.LaunchGeneration > 0 &&
        PreCommitSnapshot.PeerVisibleProof &&
        PreCommitSnapshot.PeerReceived &&
        PreCommitSnapshot.AnswerReceived &&
        PreCommitSnapshot.TunaPathLeaseRequired &&
        PreCommitSnapshot.TunaPathLeaseCurrent &&
        !string.IsNullOrWhiteSpace(PreCommitSnapshot.PathProbeId) &&
        PreCommitSnapshot.PathProbeTransport == FileTransferTransportKind.Tuna &&
        string.Equals(
            PeerProofEvidence.SessionId,
            PreCommitSnapshot.SessionId,
            StringComparison.Ordinal) &&
        string.Equals(
            PeerProofEvidence.TransferId,
            PreCommitSnapshot.TransferId,
            StringComparison.Ordinal) &&
        PreCommitSnapshot.TargetRoute == FileTransferRoute.FileTunaV4 &&
        PreCommitSnapshot.ProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
        PreCommitSnapshot.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
        PreCommitSnapshot.TargetTransport == FileTransferTransportKind.Tuna &&
        string.Equals(PreCommitSnapshot.PathProbeState, "Acked", StringComparison.OrdinalIgnoreCase);
}

internal sealed record PostTunaFallbackPeerProofFrameIdentity(
    string FrameType,
    long TransportEpoch,
    string Sha256)
{
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(FrameType) &&
        TransportEpoch > 0 &&
        Sha256.Length == 64;

    public bool Matches(FileTransferDataFrame frame)
        => TryCreate(frame, out var candidate) && candidate == this;

    public static PostTunaFallbackPeerProofFrameIdentity Create(FileTransferDataFrame frame)
        => TryCreate(frame, out var identity)
            ? identity
            : throw new ArgumentException("The frame is not a canonical post-Tuna fallback peer proof.", nameof(frame));

    public static bool TryCreate(
        FileTransferDataFrame? frame,
        out PostTunaFallbackPeerProofFrameIdentity identity)
    {
        identity = null!;
        var transportEpoch = frame switch
        {
            FileTransferReceiverStateFrameV6 state => state.TransportEpoch,
            FileTransferFrontierRequestFrameV6 request => request.TransportEpoch,
            _ => 0,
        };
        if (frame is null ||
            transportEpoch <= 0 ||
            string.IsNullOrWhiteSpace(frame.Type))
        {
            return false;
        }

        try
        {
            var payload = FileTransferDataFrameCodec.Serialize(frame);
            var hash = System.Security.Cryptography.SHA256.HashData(payload);
            identity = new PostTunaFallbackPeerProofFrameIdentity(
                frame.Type,
                transportEpoch,
                Convert.ToHexString(hash).ToLowerInvariant());
            return identity.IsComplete;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException)
        {
            return false;
        }
    }
}

internal interface IRuntimeUnlockRouteCommitProofProvider
{
    bool NotifyRuntimeUnlockPathProbeStarted(
        string sessionId,
        string transferId,
        long transportEpoch,
        string probeId,
        FileTransferTransportKind targetTransport,
        string reason,
        RuntimeUnlockPeerPreCommitAnswerAcceptanceScope currentReceiveScope =
            default,
        long callbackTransactionGeneration = 0,
        long callbackOfferGeneration = 0,
        long callbackTunaPathLeaseGeneration = -1,
        long expectedLocalTransactionGeneration = 0,
        long expectedLocalOfferGeneration = 0,
        long expectedLocalTunaPathLeaseGeneration = -1,
        string? expectedLocalTunaPathLeaseListenerRunId = null,
        RuntimeUnlockPathProbeState? expectedLocalPathProbeState = null);

    bool NotifyRuntimeUnlockPathProbeResult(
        string sessionId,
        string transferId,
        long transportEpoch,
        string probeId,
        FileTransferTransportKind targetTransport,
        bool acked,
        string reason,
        RuntimeUnlockPeerPreCommitAnswerAcceptanceScope currentReceiveScope =
            default,
        long callbackTransactionGeneration = 0,
        long callbackOfferGeneration = 0,
        long callbackTunaPathLeaseGeneration = -1,
        long expectedLocalTransactionGeneration = 0,
        long expectedLocalOfferGeneration = 0,
        long expectedLocalTunaPathLeaseGeneration = -1,
        string? expectedLocalTunaPathLeaseListenerRunId = null,
        RuntimeUnlockPathProbeState? expectedLocalPathProbeState = null);

    bool TryGetRuntimeUnlockRouteCommitProof(
        string sessionId,
        string transferId,
        out RuntimeUnlockRouteCommitSnapshot snapshot);

    void NotifyRuntimeUnlockRouteCommitResult(
        string sessionId,
        string transferId,
        FileTransferDirection direction,
        long transactionGeneration,
        long offerGeneration,
        bool accepted,
        string reason);
}

internal enum RuntimeUnlockPeerPreCommitAnswerAcceptanceStatus
{
    NotApplicable = 0,
    Accepted = 1,
    Idempotent = 2,
    Rejected = 3,
    Held = 4,
}

internal enum RuntimeUnlockResponderPeerProbeTransitionMode
{
    None = 0,
    InitialRegularV4 = 1,
    PostFallbackV6Paired = 2,
}

internal readonly record struct RuntimeUnlockPeerPreCommitAnswerAcceptanceScope(
    RuntimeOwnerToken RuntimeOwner,
    RoutePublicationToken RoutePublication,
    int TerminalGeneration,
    int LegGeneration,
    long TransportEpoch,
    int BridgeRecoveryGeneration,
    string CheckpointRequestId,
    int CheckpointGeneration,
    string DataSessionIdentity,
    long DataSessionGeneration,
    RuntimeUnlockAdmissionToken Admission = default,
    RuntimeUnlockResponderPeerProbeTransitionMode ResponderTransitionMode =
        RuntimeUnlockResponderPeerProbeTransitionMode.PostFallbackV6Paired)
{
    public bool IsDefault =>
        RuntimeOwner.IsDefault ||
        RoutePublication.IsDefault ||
        Admission.IsDefault ||
        RoutePublication.RuntimeOwner != RuntimeOwner ||
        Admission.RuntimeOwner != RuntimeOwner ||
        RoutePublication.TerminalGeneration != TerminalGeneration ||
        Admission.TerminalGeneration != TerminalGeneration ||
        RoutePublication.LegGeneration != LegGeneration ||
        Admission.LegGeneration != LegGeneration ||
        LegGeneration <= 0 ||
        string.IsNullOrWhiteSpace(DataSessionIdentity) ||
        DataSessionGeneration <= 0 ||
        !HasValidResponderTransitionModeScope;

    private bool HasValidResponderTransitionModeScope =>
        ResponderTransitionMode switch
        {
            RuntimeUnlockResponderPeerProbeTransitionMode.InitialRegularV4 =>
                RuntimeOwner.Direction == FileTransferDirection.Inbound &&
                RuntimeOwner.Route == FileTransferRoute.RegularNknV4Fast &&
                string.Equals(
                    RuntimeOwner.RouteToken,
                    FileTransferRouteResolver.RegularNknV4FastToken,
                    StringComparison.Ordinal) &&
                RuntimeOwner.ProtocolVersion ==
                    FileTransferProtocol.ProtocolVersionV4 &&
                RuntimeOwner.LiveRouteEpoch == 0 &&
                RoutePublication.Route == RuntimeOwner.Route &&
                string.Equals(
                    RoutePublication.RouteToken,
                    RuntimeOwner.RouteToken,
                    StringComparison.Ordinal) &&
                RoutePublication.ProtocolVersion ==
                    RuntimeOwner.ProtocolVersion &&
                RoutePublication.LiveRouteEpoch == 0 &&
                Admission.Route == RuntimeOwner.Route &&
                string.Equals(
                    Admission.RouteToken,
                    RuntimeOwner.RouteToken,
                    StringComparison.Ordinal) &&
                Admission.ProtocolVersion == RuntimeOwner.ProtocolVersion &&
                Admission.LiveRouteEpoch == 0 &&
                TransportEpoch == 0 &&
                BridgeRecoveryGeneration == 0 &&
                string.Equals(
                    CheckpointRequestId,
                    "(none)",
                    StringComparison.Ordinal) &&
                CheckpointGeneration == 0,
            RuntimeUnlockResponderPeerProbeTransitionMode
                    .PostFallbackV6Paired =>
                RuntimeOwner.Direction == FileTransferDirection.Inbound &&
                RuntimeOwner.Route == FileTransferRoute.PostTunaFallbackV6 &&
                string.Equals(
                    RuntimeOwner.RouteToken,
                    FileTransferRouteResolver.PostTunaFallbackV6Token,
                    StringComparison.Ordinal) &&
                RuntimeOwner.ProtocolVersion ==
                    FileTransferProtocol.ProtocolVersionV6 &&
                RoutePublication.Route == RuntimeOwner.Route &&
                string.Equals(
                    RoutePublication.RouteToken,
                    RuntimeOwner.RouteToken,
                    StringComparison.Ordinal) &&
                RoutePublication.ProtocolVersion ==
                    RuntimeOwner.ProtocolVersion &&
                RoutePublication.LiveRouteEpoch ==
                    RuntimeOwner.LiveRouteEpoch &&
                Admission.Route == RuntimeOwner.Route &&
                string.Equals(
                    Admission.RouteToken,
                    RuntimeOwner.RouteToken,
                    StringComparison.Ordinal) &&
                Admission.ProtocolVersion == RuntimeOwner.ProtocolVersion &&
                Admission.LiveRouteEpoch == RuntimeOwner.LiveRouteEpoch &&
                TransportEpoch > 0 &&
                string.IsNullOrWhiteSpace(CheckpointRequestId) is false,
            _ => false,
        };
}

internal sealed record RuntimeUnlockPeerPreCommitAnswerAcceptanceReceipt(
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    RuntimeUnlockAdmissionToken AdmissionToken,
    long PeerTransactionGeneration,
    long PeerOfferGeneration,
    long PeerTunaPathLeaseGeneration,
    string ProbeId,
    string ProbeFrameFingerprint,
    FileTransferTransportKind ReceivedTransport,
    long ReceivedUtcMs,
    long ProbeAckSentUtcMs,
    long DataSessionGeneration,
    RuntimeUnlockPeerPreCommitAnswerAcceptanceScope ReceiveScope = default);

internal sealed record RuntimeUnlockPeerPreCommitAnswerAcceptanceDecision(
    RuntimeUnlockPeerPreCommitAnswerAcceptanceReceipt Receipt,
    RuntimeUnlockPeerPreCommitAnswerAcceptanceStatus Status,
    string Reason,
    long LocalTransactionGeneration = 0,
    long LocalOfferGeneration = 0,
    long LocalTunaPathLeaseGeneration = 0,
    long PendingAnswerAckGeneration = 0,
    long LaunchGeneration = 0);

internal interface IRuntimeUnlockPeerPreCommitAnswerAcceptanceObserver
{
    RuntimeUnlockPeerPreCommitAnswerAcceptanceDecision ObserveRuntimeUnlockPeerPreCommitAnswerAcceptance(
        RuntimeUnlockPeerPreCommitAnswerAcceptanceReceipt receipt);
}

internal enum RuntimeUnlockRetryReleaseKind
{
    Unknown = 0,
    RegularV4RepairSettlement = 1,
    ReceiveRecoveryLeaseClosure = 2,
    FallbackPairedTailYield = 3,
}

internal enum RuntimeUnlockRetryDispatchDecisionStatus
{
    Held = 0,
    Granted = 1,
    Rejected = 2,
}

internal enum RuntimeUnlockRetryDispatchDecisionStageStatus
{
    Rejected = 0,
    Staged = 1,
    Idempotent = 2,
}

internal enum RuntimeUnlockRetryDispatchInstallStatus
{
    Rejected = 0,
    Installed = 1,
    Idempotent = 2,
}

internal enum RuntimeUnlockRetryDispatchStageDisposition
{
    Unknown = 0,
    Retained = 1,
    AlreadyInstalled = 2,
    Rejected = 3,
    Cleared = 4,
}

internal enum RuntimeUnlockRetryDispatchSettlementOutcome
{
    DispatchObserved = 0,
    MissingDispatch = 1,
    AuthorityRejected = 2,
    StaleRejected = 3,
    TerminalRejected = 4,
    Superseded = 5,
    TransferTerminal = 6,
}

internal enum RuntimeUnlockRetryDispatchSettlementDecisionStatus
{
    Accepted = 0,
    Idempotent = 1,
    Rejected = 2,
}

internal enum RuntimeUnlockRetryDispatchAttemptPhase
{
    Granted = 0,
    AliasBound = 1,
    SendInFlight = 2,
    Settled = 3,
}

internal readonly record struct RuntimeUnlockRetryDispatchAttempt(
    FileTransferAuthorityId AuthorityId,
    long OfferGeneration,
    string OfferNonce);

internal readonly record struct FallbackPairedTailYieldReleaseEvidence(
    FallbackTailYieldDecision LocalDecision,
    FileTransferAuthorityId PeerAuthorityId,
    FileTransferAuthorityId? PeerPredecessorAuthorityId,
    long PeerYieldGeneration,
    string PeerDirection,
    string PeerOwnerKey,
    string PeerPairKey,
    int PeerLiveRouteEpoch,
    int PeerLegGeneration,
    int PeerBridgeRecoveryGeneration,
    long PeerTransportEpoch,
    string PeerCheckpointRequestIdSnapshot)
{
    public bool IsDefault => LocalDecision.IsDefault ||
                             PeerAuthorityId.IsDefault ||
                             PeerYieldGeneration <= 0 ||
                             string.IsNullOrWhiteSpace(PeerDirection) ||
                             string.IsNullOrWhiteSpace(PeerOwnerKey) ||
                             string.IsNullOrWhiteSpace(PeerPairKey) ||
                             PeerLiveRouteEpoch <= 0 ||
                             PeerLegGeneration <= 0 ||
                             PeerBridgeRecoveryGeneration < 0 ||
                             PeerTransportEpoch <= 0 ||
                             string.IsNullOrWhiteSpace(PeerCheckpointRequestIdSnapshot);
}

internal readonly record struct RuntimeUnlockRetryDispatchInitialEvaluation(
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    FileTransferRoutePublicationAuthority SourcePublicationAuthority,
    long RecoveryContractGeneration,
    long RetiredOfferGeneration,
    string RetryReason,
    string RecoveryReason,
    int RegularV4LivenessGeneration,
    int BridgeDecisionGeneration)
{
    public FallbackPairedTailYieldReleaseEvidence? FallbackPairedTailYieldEvidence { get; init; }

    public RuntimeUnlockRetryDispatchListenerRearmRequirement
        ListenerRearmRequirement { get; init; }

    public RuntimeUnlockRetryDispatchListenerExecutionMode
        ListenerExecutionMode { get; init; }

    public bool IsDefault => string.IsNullOrWhiteSpace(SessionId) ||
                             string.IsNullOrWhiteSpace(TransferId) ||
                             SourcePublicationAuthority is null ||
                             RecoveryContractGeneration <= 0 ||
                             RetiredOfferGeneration < 0;
}

internal readonly record struct RuntimeUnlockRetryDispatchAuthorityRequest(
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    FileTransferRoutePublicationAuthority SourcePublicationAuthority,
    long RecoveryContractGeneration,
    long RetiredOfferGeneration,
    int DispatchAttemptGeneration,
    string RetryReason,
    string RecoveryReason,
    int RegularV4LivenessGeneration,
    int BridgeDecisionGeneration,
    FileTransferAuthorityId? PredecessorAuthorityId = null,
    bool RequestActivePredecessorSupersession = false,
    int MissingDispatchSuccessorGeneration = 0)
{
    public FallbackPairedTailYieldReleaseEvidence? FallbackPairedTailYieldEvidence { get; init; }

    public RuntimeUnlockRetryDispatchListenerRearmRequirement
        ListenerRearmRequirement { get; init; }

    public RuntimeUnlockRetryDispatchListenerExecutionMode
        ListenerExecutionMode { get; init; }

    public bool IsDefault => string.IsNullOrWhiteSpace(SessionId) ||
                             string.IsNullOrWhiteSpace(TransferId) ||
                             SourcePublicationAuthority is null ||
                             RecoveryContractGeneration <= 0 ||
                             DispatchAttemptGeneration <= 0;
}

internal enum RuntimeUnlockRetryDispatchListenerRearmRequirement
{
    None = 0,
    Required = 1,
    RequiredBeforeDispatch = 2,
}

internal enum RuntimeUnlockRetryDispatchListenerExecutionMode
{
    Default = 0,
    AdoptCurrentCoordinatorScoped = 1,
}

internal readonly record struct RuntimeUnlockRetryDispatchAuthority(
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    RuntimeOwnerToken RuntimeOwner,
    RoutePublicationToken SourcePublicationAuthority,
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
    long RecoveryContractGeneration,
    long RetiredOfferGeneration,
    int DispatchAttemptGeneration,
    int MissingDispatchSuccessorGeneration,
    string RetryReason,
    string RecoveryReason,
    RuntimeUnlockRetryReleaseKind ReleaseKind,
    FileTransferAuthorityId? ReleaseAuthorityId,
    int ReleaseGeneration,
    string ReleaseOutcome,
    FileTransferRoute TargetRoute,
    int TargetProtocolVersion,
    FileTransferTransportKind TargetTransport,
    int AuthorityGeneration,
    FileTransferAuthorityId AuthorityId,
    FileTransferAuthorityId? PredecessorAuthorityId)
{
    public RuntimeUnlockRetryDispatchListenerRearmRequirement
        ListenerRearmRequirement { get; init; }

    public RuntimeUnlockRetryDispatchListenerExecutionMode
        ListenerExecutionMode { get; init; }

    public long ReceiveRecoveryOperationGeneration { get; init; }

    public long ReceiveRecoveryDataSessionGeneration { get; init; }

    public bool IsDefault => AuthorityId.IsDefault ||
                             RuntimeOwner.IsDefault ||
                             SourcePublicationAuthority.IsDefault ||
                             RecoveryContractGeneration <= 0 ||
                             DispatchAttemptGeneration <= 0 ||
                             AuthorityGeneration <= 0 ||
                             ReleaseKind ==
                                 RuntimeUnlockRetryReleaseKind
                                     .ReceiveRecoveryLeaseClosure &&
                             (ReceiveRecoveryOperationGeneration <= 0 ||
                              ReceiveRecoveryDataSessionGeneration <= 0);
}

internal readonly record struct RuntimeUnlockRetryDispatchSettlementReport(
    RuntimeUnlockRetryDispatchAuthority Authority,
    RuntimeUnlockRetryDispatchSettlementOutcome Outcome,
    string Reason)
{
    public long OfferGeneration { get; init; }

    public string? OfferNonce { get; init; }

    public bool DispatchObserved { get; init; }

    public FileTransferAuthorityId? SuccessorAuthorityId { get; init; }

    public bool IsDefault => Authority.IsDefault || string.IsNullOrWhiteSpace(Reason);
}

internal readonly record struct RuntimeUnlockRetryDispatchInstallReceipt(
    RuntimeUnlockRetryDispatchAuthority Authority,
    long InstallGeneration)
{
    public RuntimeUnlockRetryDispatchAuthorityRequest Request { get; init; }

    public RuntimeUnlockRetryDispatchDecisionStageStatus StageStatus { get; init; }

    public RuntimeUnlockRetryDispatchStageDisposition StageDisposition { get; init; }

    public bool StageCleared { get; init; }

    public long StagedUtcMs { get; init; }

    public long StageExpiresUtcMs { get; init; }

    public long DispatchDeadlineUtcMs { get; init; }

    public bool IsDefault => Authority.IsDefault || InstallGeneration <= 0;
}

internal readonly record struct RuntimeUnlockRetryDispatchDeadlineEvaluation(
    RuntimeUnlockRetryDispatchAuthority Authority,
    RuntimeUnlockRetryDispatchInstallReceipt InstallReceipt,
    long ObservedUtcMs,
    string Reason)
{
    public bool IsDefault =>
        Authority.IsDefault ||
        InstallReceipt.IsDefault ||
        InstallReceipt.Authority != Authority ||
        ObservedUtcMs <= 0 ||
        string.IsNullOrWhiteSpace(Reason);
}

internal readonly record struct RuntimeUnlockRetryDispatchExecutionObservation(
    RuntimeUnlockRetryDispatchAuthority Authority,
    RuntimeUnlockRetryDispatchAttemptPhase AttemptPhase,
    long OfferGeneration,
    string? OfferNonce,
    bool DispatchObserved)
{
    public RuntimeUnlockRetryDispatchInstallReceipt InstallReceipt { get; init; }

    public bool IsDefault =>
        Authority.IsDefault ||
        InstallReceipt.IsDefault ||
        InstallReceipt.Authority != Authority;
}

internal readonly record struct RuntimeUnlockRetryDispatchSuccessorEvaluation(
    RuntimeUnlockRetryDispatchAuthority PredecessorAuthority,
    long RetiredOfferGeneration)
{
    public long PredecessorOfferGeneration { get; init; }

    public string? PredecessorOfferNonce { get; init; }

    public bool PredecessorDispatchObserved { get; init; }

    public bool IsDefault => PredecessorAuthority.IsDefault ||
                             RetiredOfferGeneration < 0;
}

internal sealed class RuntimeUnlockRetryDispatchContract
{
    private readonly object aliasGate = new();
    private long offerGeneration;
    private string? offerNonce;
    private RuntimeUnlockRetryDispatchAttemptPhase attemptPhase = RuntimeUnlockRetryDispatchAttemptPhase.Granted;
    private int grantLogged;
    private int dispatchLogged;
    private int settlementLogged;

    public RuntimeUnlockRetryDispatchContract(
        RuntimeUnlockRetryDispatchAuthority authority,
        bool requestActivePredecessorSupersession = false,
        int regularV4LivenessGeneration = 0,
        int requestBridgeDecisionGeneration = 0)
    {
        if (authority.IsDefault)
        {
            throw new ArgumentException("Runtime-unlock retry authority must be non-default.", nameof(authority));
        }

        Authority = authority;
        RequestActivePredecessorSupersession = requestActivePredecessorSupersession;
        RegularV4LivenessGeneration = regularV4LivenessGeneration;
        RequestBridgeDecisionGeneration = requestBridgeDecisionGeneration;
        Lifecycle = new FileTransferAuthorityLifecycle<RuntimeUnlockRetryDispatchSettlementOutcome>(
            authority.AuthorityId);
    }

    public RuntimeUnlockRetryDispatchAuthority Authority { get; }

    public bool RequestActivePredecessorSupersession { get; }

    public int RegularV4LivenessGeneration { get; }

    public int RequestBridgeDecisionGeneration { get; }

    public FileTransferAuthorityLifecycle<RuntimeUnlockRetryDispatchSettlementOutcome> Lifecycle { get; }

    public long OfferGeneration
    {
        get
        {
            lock (aliasGate)
            {
                return offerGeneration;
            }
        }
    }

    public string? OfferNonce
    {
        get
        {
            lock (aliasGate)
            {
                return offerNonce;
            }
        }
    }

    public RuntimeUnlockRetryDispatchAttemptPhase AttemptPhase
    {
        get
        {
            lock (aliasGate)
            {
                return Lifecycle.IsSettled
                    ? RuntimeUnlockRetryDispatchAttemptPhase.Settled
                    : attemptPhase;
            }
        }
    }

    public bool TryBindOfferAlias(long generation, string nonce)
    {
        if (generation <= 0 || string.IsNullOrWhiteSpace(nonce))
        {
            return false;
        }

        lock (aliasGate)
        {
            if (Lifecycle.IsSettled)
            {
                return false;
            }

            if (offerGeneration == 0)
            {
                offerGeneration = generation;
                offerNonce = nonce.Trim();
                attemptPhase = RuntimeUnlockRetryDispatchAttemptPhase.AliasBound;
                return true;
            }

            return offerGeneration == generation &&
                   string.Equals(offerNonce, nonce.Trim(), StringComparison.Ordinal);
        }
    }

    public bool TryBeginSend(out RuntimeUnlockRetryDispatchAttempt attempt)
    {
        attempt = default;
        lock (aliasGate)
        {
            if (Lifecycle.IsSettled ||
                offerGeneration <= 0 ||
                string.IsNullOrWhiteSpace(offerNonce) ||
                attemptPhase == RuntimeUnlockRetryDispatchAttemptPhase.Settled)
            {
                return false;
            }

            if (attemptPhase != RuntimeUnlockRetryDispatchAttemptPhase.AliasBound)
            {
                return false;
            }

            attemptPhase = RuntimeUnlockRetryDispatchAttemptPhase.SendInFlight;
            attempt = new RuntimeUnlockRetryDispatchAttempt(
                Authority.AuthorityId,
                offerGeneration,
                offerNonce);
            return true;
        }
    }

    public bool TryGetInFlightAttempt(out RuntimeUnlockRetryDispatchAttempt attempt)
    {
        attempt = default;
        lock (aliasGate)
        {
            if (Lifecycle.IsSettled ||
                attemptPhase != RuntimeUnlockRetryDispatchAttemptPhase.SendInFlight ||
                offerGeneration <= 0 ||
                string.IsNullOrWhiteSpace(offerNonce))
            {
                return false;
            }

            attempt = new RuntimeUnlockRetryDispatchAttempt(
                Authority.AuthorityId,
                offerGeneration,
                offerNonce);
            return true;
        }
    }

    public bool MatchesAttempt(RuntimeUnlockRetryDispatchAttempt attempt)
    {
        lock (aliasGate)
        {
            return attempt.AuthorityId == Authority.AuthorityId &&
                   offerGeneration == attempt.OfferGeneration &&
                   string.Equals(offerNonce, attempt.OfferNonce, StringComparison.Ordinal);
        }
    }

    public bool TryCreateObservedSettlementProposal(
        RuntimeUnlockRetryDispatchAttempt attempt,
        string reason,
        out RuntimeUnlockRetryDispatchSettlementReport report)
        => TryCreateAttemptSettlementProposal(
            attempt,
            RuntimeUnlockRetryDispatchSettlementOutcome.DispatchObserved,
            reason,
            dispatched: true,
            out report);

    public bool TryCreateMissingDispatchSettlementProposal(
        RuntimeUnlockRetryDispatchAttempt attempt,
        string reason,
        out RuntimeUnlockRetryDispatchSettlementReport report)
        => TryCreateAttemptSettlementProposal(
            attempt,
            RuntimeUnlockRetryDispatchSettlementOutcome.MissingDispatch,
            reason,
            dispatched: false,
            out report);

    public bool TryCreatePreOfferMissingDispatchSettlementProposal(
        string reason,
        out RuntimeUnlockRetryDispatchSettlementReport report)
    {
        report = default;
        lock (aliasGate)
        {
            var unaliasedGrant =
                attemptPhase == RuntimeUnlockRetryDispatchAttemptPhase.Granted &&
                offerGeneration == 0 &&
                offerNonce is null;
            var aliasedBeforeSend =
                attemptPhase == RuntimeUnlockRetryDispatchAttemptPhase.AliasBound &&
                offerGeneration > 0 &&
                !string.IsNullOrWhiteSpace(offerNonce);
            if (Lifecycle.IsSettled || (!unaliasedGrant && !aliasedBeforeSend))
            {
                return false;
            }

            report = CreateSettlementReportUnsafe(
                RuntimeUnlockRetryDispatchSettlementOutcome.MissingDispatch,
                reason,
                dispatchObserved: false,
                successorAuthorityId: null);
            return true;
        }
    }

    public bool TryCreateTerminalSettlementProposal(
        RuntimeUnlockRetryDispatchSettlementOutcome outcome,
        string reason,
        out RuntimeUnlockRetryDispatchSettlementReport report)
    {
        report = default;
        if (outcome is not (
                RuntimeUnlockRetryDispatchSettlementOutcome.AuthorityRejected or
                RuntimeUnlockRetryDispatchSettlementOutcome.StaleRejected or
                RuntimeUnlockRetryDispatchSettlementOutcome.TerminalRejected or
                RuntimeUnlockRetryDispatchSettlementOutcome.TransferTerminal))
        {
            return false;
        }

        lock (aliasGate)
        {
            if (Lifecycle.IsSettled)
            {
                return false;
            }

            report = CreateSettlementReportUnsafe(
                outcome,
                reason,
                dispatchObserved: false,
                successorAuthorityId: null);
            return true;
        }
    }

    public bool TryCreateSupersededSettlementProposal(
        string reason,
        FileTransferAuthorityId successorAuthorityId,
        out RuntimeUnlockRetryDispatchSettlementReport report)
    {
        report = default;
        lock (aliasGate)
        {
            if (attemptPhase == RuntimeUnlockRetryDispatchAttemptPhase.SendInFlight ||
                Lifecycle.IsSettled)
            {
                return false;
            }

            report = CreateSettlementReportUnsafe(
                RuntimeUnlockRetryDispatchSettlementOutcome.Superseded,
                reason,
                dispatchObserved: false,
                successorAuthorityId);
            return true;
        }
    }

    private bool TryCreateAttemptSettlementProposal(
        RuntimeUnlockRetryDispatchAttempt attempt,
        RuntimeUnlockRetryDispatchSettlementOutcome outcome,
        string reason,
        bool dispatched,
        out RuntimeUnlockRetryDispatchSettlementReport report)
    {
        report = default;
        lock (aliasGate)
        {
            if (attemptPhase != RuntimeUnlockRetryDispatchAttemptPhase.SendInFlight ||
                Lifecycle.IsSettled ||
                attempt.AuthorityId != Authority.AuthorityId ||
                offerGeneration != attempt.OfferGeneration ||
                !string.Equals(offerNonce, attempt.OfferNonce, StringComparison.Ordinal))
            {
                return false;
            }

            report = CreateSettlementReportUnsafe(
                outcome,
                reason,
                dispatched,
                successorAuthorityId: null);
            return true;
        }
    }

    public bool MatchesOfferAlias(long generation, string? nonce = null)
    {
        lock (aliasGate)
        {
            return offerGeneration > 0 &&
                   offerGeneration == generation &&
                   (string.IsNullOrWhiteSpace(nonce) ||
                    string.Equals(offerNonce, nonce.Trim(), StringComparison.Ordinal));
        }
    }

    public bool TryMarkGrantLogged()
        => Interlocked.CompareExchange(ref grantLogged, 1, 0) == 0;

    public bool TryMarkDispatchLogged()
        => Interlocked.CompareExchange(ref dispatchLogged, 1, 0) == 0;

    public bool TryMarkSettlementLogged()
        => Interlocked.CompareExchange(ref settlementLogged, 1, 0) == 0;

    public bool TryCreateSettlementReport(
        out RuntimeUnlockRetryDispatchSettlementReport report)
    {
        report = default;
        lock (aliasGate)
        {
            if (!Lifecycle.IsSettled ||
                Lifecycle.SettlementOutcome is not { } outcome)
            {
                return false;
            }

            report = CreateSettlementReportUnsafe(
                outcome,
                Lifecycle.SettlementReason,
                Lifecycle.DispatchObserved,
                Lifecycle.SuccessorAuthorityId);
            return true;
        }
    }

    public bool TryApplySettlementReport(
        RuntimeUnlockRetryDispatchSettlementReport report)
    {
        lock (aliasGate)
        {
            if (report.IsDefault ||
                report.Authority != Authority ||
                report.OfferGeneration != offerGeneration ||
                !string.Equals(
                    report.OfferNonce,
                    offerNonce,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    report.Reason,
                    NormalizeSettlementReason(report.Reason),
                    StringComparison.Ordinal) ||
                report.DispatchObserved !=
                    (report.Outcome ==
                     RuntimeUnlockRetryDispatchSettlementOutcome
                         .DispatchObserved) ||
                (report.Outcome ==
                     RuntimeUnlockRetryDispatchSettlementOutcome.Superseded
                    ? report.SuccessorAuthorityId is not
                        { IsDefault: false } ||
                      attemptPhase ==
                        RuntimeUnlockRetryDispatchAttemptPhase.SendInFlight
                    : report.SuccessorAuthorityId is not null))
            {
                return false;
            }

            if (Lifecycle.IsSettled)
            {
                return Lifecycle.SettlementOutcome == report.Outcome &&
                       string.Equals(
                           NormalizeSettlementReason(
                               Lifecycle.SettlementReason),
                           report.Reason,
                           StringComparison.Ordinal) &&
                       Lifecycle.DispatchObserved ==
                           report.DispatchObserved &&
                       Lifecycle.SuccessorAuthorityId ==
                           report.SuccessorAuthorityId;
            }

            if (report.Outcome ==
                    RuntimeUnlockRetryDispatchSettlementOutcome
                        .DispatchObserved &&
                attemptPhase !=
                    RuntimeUnlockRetryDispatchAttemptPhase.SendInFlight)
            {
                return false;
            }

            if (report.Outcome ==
                    RuntimeUnlockRetryDispatchSettlementOutcome
                        .MissingDispatch &&
                !((attemptPhase ==
                       RuntimeUnlockRetryDispatchAttemptPhase.SendInFlight &&
                   offerGeneration > 0 &&
                   !string.IsNullOrWhiteSpace(offerNonce)) ||
                  (attemptPhase ==
                       RuntimeUnlockRetryDispatchAttemptPhase.Granted &&
                   offerGeneration == 0 &&
                   offerNonce is null) ||
                  (attemptPhase ==
                       RuntimeUnlockRetryDispatchAttemptPhase.AliasBound &&
                   offerGeneration > 0 &&
                   !string.IsNullOrWhiteSpace(offerNonce))))
            {
                return false;
            }

            var applied = report.DispatchObserved
                ? Lifecycle.TryMarkDispatchedAndSettle(
                    report.Outcome,
                    report.Reason,
                    report.SuccessorAuthorityId)
                : Lifecycle.TrySettle(
                    report.Outcome,
                    report.Reason,
                    report.SuccessorAuthorityId);
            if (!applied)
            {
                return false;
            }

            attemptPhase = RuntimeUnlockRetryDispatchAttemptPhase.Settled;
            return true;
        }
    }

    private RuntimeUnlockRetryDispatchSettlementReport
        CreateSettlementReportUnsafe(
            RuntimeUnlockRetryDispatchSettlementOutcome outcome,
            string? reason,
            bool dispatchObserved,
            FileTransferAuthorityId? successorAuthorityId)
        => new(
            Authority,
            outcome,
            NormalizeSettlementReason(reason))
        {
            OfferGeneration = offerGeneration,
            OfferNonce = offerNonce,
            DispatchObserved = dispatchObserved,
            SuccessorAuthorityId =
                successorAuthorityId is { IsDefault: false }
                    ? successorAuthorityId
                    : null,
        };

    private static string NormalizeSettlementReason(string? reason)
        => string.IsNullOrWhiteSpace(reason) ? "unknown" : reason.Trim();
}

internal readonly record struct RuntimeUnlockRetryDispatchDecision(
    RuntimeUnlockRetryDispatchDecisionStatus Status,
    string Reason,
    RuntimeUnlockRetryDispatchContract? Contract)
{
    public bool Granted => Status == RuntimeUnlockRetryDispatchDecisionStatus.Granted && Contract is not null;

    public RuntimeUnlockRetryDispatchAuthorityRequest Request { get; init; }

    public FileTransferCoordinatorState State { get; init; } = null!;
}

internal readonly record struct RuntimeUnlockRetryDispatchDecisionStageResult(
    RuntimeUnlockRetryDispatchDecisionStageStatus Status,
    string Reason,
    RuntimeUnlockRetryDispatchDecision Decision)
{
    public RuntimeUnlockRetryDispatchStageDisposition StageDisposition { get; init; }

    public bool StageCleared { get; init; }

    public long StagedUtcMs { get; init; }

    public long StageExpiresUtcMs { get; init; }

    public bool Accepted =>
        Decision.Granted &&
        !StageCleared &&
        Status is
            RuntimeUnlockRetryDispatchDecisionStageStatus.Staged or
            RuntimeUnlockRetryDispatchDecisionStageStatus.Idempotent &&
        (
            StageDisposition == RuntimeUnlockRetryDispatchStageDisposition.Retained &&
            StagedUtcMs > 0 ||
            Status == RuntimeUnlockRetryDispatchDecisionStageStatus.Idempotent &&
            StageDisposition == RuntimeUnlockRetryDispatchStageDisposition.AlreadyInstalled &&
            StagedUtcMs == 0 &&
            StageExpiresUtcMs == 0
        );
}

internal readonly record struct RuntimeUnlockRetryDispatchInstallResult(
    RuntimeUnlockRetryDispatchInstallStatus Status,
    string Reason,
    RuntimeUnlockRetryDispatchInstallReceipt Receipt)
{
    public bool Accepted =>
        !Receipt.IsDefault &&
        Status is
            RuntimeUnlockRetryDispatchInstallStatus.Installed or
            RuntimeUnlockRetryDispatchInstallStatus.Idempotent;
}

internal enum RuntimeUnlockRetryDispatchScopeInvalidationStatus
{
    Rejected = 0,
    Applied = 1,
    Idempotent = 2,
}

internal enum RuntimeUnlockRetryDispatchScopeInvalidationStageStatus
{
    Rejected = 0,
    Staged = 1,
    Idempotent = 2,
}

internal enum RuntimeUnlockRetryDispatchScopeInvalidationKind
{
    FullScope = 0,
    PendingStageExpiry = 1,
}

internal sealed record RuntimeUnlockRetryDispatchScopeInvalidationCommand(
    RuntimeUnlockRetryLifecycleAttemptSnapshot? PendingAttempt,
    RuntimeUnlockRetryLifecycleAttemptSnapshot? InstalledAttempt,
    RuntimeUnlockRetryLifecycleAttemptSnapshot? RetainedPredecessor,
    RuntimeUnlockRetryLifecycleBoundedClosure? BoundedClosure,
    long InvalidationGeneration,
    string Reason,
    FileTransferAuthorityId CommandId,
    RuntimeUnlockRetryDispatchScopeInvalidationKind Kind =
        RuntimeUnlockRetryDispatchScopeInvalidationKind.FullScope)
{
    public bool IsDefault =>
        InvalidationGeneration <= 0 ||
        string.IsNullOrWhiteSpace(Reason) ||
        CommandId.IsDefault ||
        PendingAttempt is null &&
        InstalledAttempt is null &&
        RetainedPredecessor is null &&
        BoundedClosure is null;
}

internal readonly record struct RuntimeUnlockRetryDispatchScopeInvalidationStageResult(
    RuntimeUnlockRetryDispatchScopeInvalidationCommand Command,
    RuntimeUnlockRetryDispatchScopeInvalidationStageStatus Status,
    string Reason)
{
    public bool Accepted =>
        Command is not null &&
        !Command.IsDefault &&
        Status is
            RuntimeUnlockRetryDispatchScopeInvalidationStageStatus.Staged or
            RuntimeUnlockRetryDispatchScopeInvalidationStageStatus.Idempotent;
}

internal readonly record struct RuntimeUnlockRetryDispatchScopeInvalidationReceipt(
    RuntimeUnlockRetryDispatchScopeInvalidationCommand Command,
    RuntimeUnlockRetryDispatchScopeInvalidationStatus Status,
    string Reason)
{
    public bool Accepted =>
        Command is not null &&
        !Command.IsDefault &&
        Status is
            RuntimeUnlockRetryDispatchScopeInvalidationStatus.Applied or
            RuntimeUnlockRetryDispatchScopeInvalidationStatus.Idempotent;
}

internal readonly record struct RuntimeUnlockRetryDispatchSettlementDecision(
    RuntimeUnlockRetryDispatchSettlementDecisionStatus Status,
    string Reason,
    RuntimeUnlockRetryDispatchSettlementReport Report)
{
    public bool Accepted => Status == RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted;

    public FileTransferCoordinatorState State { get; init; } = null!;
}

internal sealed class RuntimeUnlockRetryDispatchAuthorityRequestedEventArgs(
    RuntimeUnlockRetryDispatchAuthorityRequest request) : EventArgs
{
    public RuntimeUnlockRetryDispatchAuthorityRequest Request { get; } = request;
}

internal sealed class RuntimeUnlockRetryDispatchInitialEvaluationRequestedEventArgs(
    RuntimeUnlockRetryDispatchInitialEvaluation evaluation) : EventArgs
{
    public RuntimeUnlockRetryDispatchInitialEvaluation Evaluation { get; } =
        evaluation;
}

internal sealed class RuntimeUnlockRetryDispatchSettlementReportedEventArgs(
    RuntimeUnlockRetryDispatchSettlementReport report) : EventArgs
{
    public RuntimeUnlockRetryDispatchSettlementReport Report { get; } = report;
}

internal sealed class RuntimeUnlockRetryDispatchDeadlineEvaluationEventArgs(
    RuntimeUnlockRetryDispatchDeadlineEvaluation evaluation) : EventArgs
{
    public RuntimeUnlockRetryDispatchDeadlineEvaluation Evaluation { get; } =
        evaluation;
}

internal sealed class RuntimeUnlockRetryDispatchExecutionObservedEventArgs(
    RuntimeUnlockRetryDispatchExecutionObservation observation) : EventArgs
{
    public RuntimeUnlockRetryDispatchExecutionObservation Observation { get; } =
        observation;

    public RuntimeUnlockRetryLifecycleTransitionDecision? Decision
    {
        get;
        set;
    }
}

internal sealed class RuntimeUnlockRetryDispatchSuccessorEvaluationEventArgs(
    RuntimeUnlockRetryDispatchSuccessorEvaluation evaluation) : EventArgs
{
    public RuntimeUnlockRetryDispatchSuccessorEvaluation Evaluation { get; } =
        evaluation;
}

internal interface IRuntimeUnlockRetryDispatchAuthorityBridge
{
    event EventHandler<RuntimeUnlockRetryDispatchInitialEvaluationRequestedEventArgs>?
        RuntimeUnlockRetryDispatchInitialEvaluationRequested;

    event EventHandler<RuntimeUnlockRetryDispatchAuthorityRequestedEventArgs>?
        RuntimeUnlockRetryDispatchAuthorityRequested;

    event EventHandler<RuntimeUnlockRetryDispatchSettlementReportedEventArgs>?
        RuntimeUnlockRetryDispatchSettlementReported;

    event EventHandler<RuntimeUnlockRetryDispatchDeadlineEvaluationEventArgs>?
        RuntimeUnlockRetryDispatchDeadlineEvaluationRequested;

    event EventHandler<RuntimeUnlockRetryDispatchExecutionObservedEventArgs>?
        RuntimeUnlockRetryDispatchExecutionObserved;

    event EventHandler<RuntimeUnlockRetryDispatchSuccessorEvaluationEventArgs>?
        RuntimeUnlockRetryDispatchSuccessorEvaluationRequested;

    bool QueueRuntimeUnlockRetryDispatchAuthorityRequest(
        RuntimeUnlockRetryDispatchAuthorityRequest request)
        => false;

    bool QueueFallbackTailYieldRetryDispatchRecheck(
        FileTransferFallbackTailYieldRetryDispatchRecheckCommand command)
        => false;

    bool ObserveRuntimeUnlockRetryDispatchNonGrantDecision(
        RuntimeUnlockRetryDispatchDecision decision);

    bool ObserveRuntimeUnlockRetryDispatchSuccessorDecision(
        RuntimeUnlockRetryDispatchSuccessorDecision decision)
        => false;

    RuntimeUnlockRetryDispatchDecisionStageResult StageRuntimeUnlockRetryDispatchDecision(
        RuntimeUnlockRetryDispatchDecision decision);

    RuntimeUnlockRetryDispatchInstallResult CommitRuntimeUnlockRetryDispatchInstall(
        RuntimeUnlockRetryDispatchInstallReceipt receipt);

    RuntimeUnlockRetryDispatchScopeInvalidationReceipt
        ApplyRuntimeUnlockRetryDispatchScopeInvalidation(
            RuntimeUnlockRetryDispatchScopeInvalidationCommand command);

    RuntimeUnlockRetryDispatchScopeInvalidationStageResult
        StageRuntimeUnlockRetryDispatchScopeInvalidation(
            RuntimeUnlockRetryDispatchScopeInvalidationCommand command);

    bool ConfirmRuntimeUnlockRetryDispatchInstall(
        RuntimeUnlockRetryDispatchInstallReceipt receipt);

    bool ObserveRuntimeUnlockRetryDispatchSettlementDecision(
        RuntimeUnlockRetryDispatchSettlementDecision decision);

}

internal enum RuntimeUnlockInitialActivationReceiveRecoveryHoldBoundary
{
    OfferPreSend = 0,
    UnobservedOfferSendFailure = 1,
    PeerResponseTimeout = 2,
    AnswerTimeout = 3,
    PayerIntentPreSend = 4,
    UnobservedPayerIntentSendFailure = 5,
}

internal readonly record struct
    RuntimeUnlockInitialActivationReceiveRecoveryExpectedScope(
        FileTransferRoutePublicationAuthority? SourcePublicationAuthority,
        long TransportEpoch,
        int BridgeRecoveryGeneration,
        string? CheckpointRequestId,
        string DataSessionIdentity,
        long DataSessionGeneration,
        FileTransferBridgeRecoveryExecutionCommand? ExpectedExecutionCommand)
{
    public bool IsDefault =>
        SourcePublicationAuthority is null ||
        string.IsNullOrWhiteSpace(SourcePublicationAuthority.SessionId) ||
        string.IsNullOrWhiteSpace(SourcePublicationAuthority.TransferId) ||
        string.IsNullOrWhiteSpace(SourcePublicationAuthority.RouteToken) ||
        SourcePublicationAuthority.ProtocolVersion <= 0 ||
        SourcePublicationAuthority.RuntimeGeneration <= 0 ||
        SourcePublicationAuthority.LegGeneration <= 0 ||
        SourcePublicationAuthority.RoutePublicationGeneration <= 0 ||
        TransportEpoch < 0 ||
        BridgeRecoveryGeneration < 0 ||
        string.IsNullOrWhiteSpace(DataSessionIdentity) ||
        DataSessionGeneration <= 0 ||
        ExpectedExecutionCommand is { IsDefault: true };
}

internal readonly record struct
    RuntimeUnlockInitialActivationReceiveRecoveryHoldRequest(
        string SessionId,
        string TransferId,
        FileTransferDirection Direction,
        string OfferNonce,
        long PayerDecisionId,
        long OfferGeneration,
        long TransactionGeneration,
        long TunaPathLeaseGeneration,
        string TunaPathLeaseListenerRunId,
        string ControlEnvelopeFingerprint,
        bool PeerVisibleDispatchObserved,
        RuntimeUnlockInitialActivationReceiveRecoveryHoldBoundary Boundary)
{
    public string SemanticOfferFingerprint { get; init; } = string.Empty;

    public long EnvelopeGeneration { get; init; }

    public long EnvelopeSentAtUnixMs { get; init; }

    public long EnvelopeExpiresAtUnixMs { get; init; }

    public RuntimeUnlockInitialActivationReceiveRecoveryExpectedScope
        ExpectedScope
    {
        get;
        init;
    }

    public bool IsDefault
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SessionId) ||
                string.IsNullOrWhiteSpace(TransferId) ||
                PayerDecisionId <= 0 ||
                ExpectedScope.IsDefault ||
                !string.Equals(
                    SessionId,
                    ExpectedScope.SourcePublicationAuthority?.SessionId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    TransferId,
                    ExpectedScope.SourcePublicationAuthority?.TransferId,
                    StringComparison.Ordinal) ||
                Direction !=
                    ExpectedScope.SourcePublicationAuthority?.Direction)
            {
                return true;
            }

            return string.IsNullOrWhiteSpace(OfferNonce) ||
                OfferGeneration <= 0 ||
                TransactionGeneration <= 0 ||
                TunaPathLeaseGeneration <= 0 ||
                string.IsNullOrWhiteSpace(TunaPathLeaseListenerRunId) ||
                string.IsNullOrWhiteSpace(ControlEnvelopeFingerprint);
        }
    }
}

internal enum RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus
{
    Held = 0,
    Released = 1,
    Rejected = 2,
    Invalidated = 3,
}

internal readonly record struct
    RuntimeUnlockInitialActivationReceiveRecoveryFallbackCheckpointBinding(
        FileTransferAuthorityId ParentAuthorityId,
        long ParentOperationGeneration,
        string? ParentCheckpointRequestId,
        string EffectiveCheckpointRequestId,
        int EffectiveCheckpointGeneration,
        long TransportEpoch,
        FileTransferReceiveRecoveryFallbackCheckpointBindingState State)
{
    public long BindingGeneration { get; init; }

    public PostTunaFallbackPeerProofFrameIdentity? FrameIdentity { get; init; }

    public bool IsDefault =>
        ParentAuthorityId.IsDefault ||
        ParentOperationGeneration <= 0 ||
        string.IsNullOrWhiteSpace(EffectiveCheckpointRequestId) ||
        EffectiveCheckpointGeneration <= 0 ||
        TransportEpoch <= 0 ||
        BindingGeneration <= 0 ||
        FrameIdentity is not { IsComplete: true } ||
        State ==
            FileTransferReceiveRecoveryFallbackCheckpointBindingState
                .Invalidated;
}

internal enum RuntimeUnlockInitialActivationOfferEnvelopeRefreshReleaseProfile
{
    RegularV4Closure = 0,
    PostTunaFallbackCheckpointProof = 1,
}

internal readonly record struct
    RuntimeUnlockInitialActivationReceiveRecoveryHoldDecision(
        RuntimeUnlockInitialActivationReceiveRecoveryHoldRequest Request,
        RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus Status,
        string Reason,
        long HoldGeneration,
        FileTransferReceiveRecoveryOperation? ReceiveRecoveryOperation,
        FileTransferReceiveRecoveryOperationAuthority?
            PreviousReceiveRecoveryAuthority = null)
{
    public RuntimeUnlockInitialActivationOfferEnvelopeRefreshCommand?
        EnvelopeRefreshCommand
    {
        get;
        init;
    }

    public bool IsDefault =>
        Request.IsDefault ||
        string.IsNullOrWhiteSpace(Reason) ||
        HoldGeneration < 0 ||
        Status == RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Held &&
        (HoldGeneration <= 0 ||
         ReceiveRecoveryOperation is not { IsDefault: false });
}

internal sealed record
    RuntimeUnlockInitialActivationOfferEnvelopeRefreshCommand(
        RuntimeUnlockInitialActivationReceiveRecoveryHoldRequest HoldRequest,
        long HoldGeneration,
        FileTransferReceiveRecoveryOperationAuthority
            ReceiveRecoveryAuthority,
        long ClosureGeneration,
        long RefreshGeneration,
        long SuccessorEnvelopeGeneration,
        long SuccessorSentAtUnixMs,
        long SuccessorExpiresAtUnixMs)
{
    public RuntimeUnlockInitialActivationOfferEnvelopeRefreshReleaseProfile
        ReleaseProfile { get; init; }

    public FileTransferReceiveRecoveryProof? ReceiveRecoveryProof { get; init; }

    public RuntimeUnlockInitialActivationReceiveRecoveryFallbackCheckpointBinding?
        FallbackCheckpointBinding { get; init; }

    public bool IsDefault =>
        HoldRequest.IsDefault ||
        HoldGeneration <= 0 ||
        ReceiveRecoveryAuthority.IsDefault ||
        RefreshGeneration <= 0 ||
        SuccessorEnvelopeGeneration != HoldRequest.EnvelopeGeneration + 1 ||
        SuccessorSentAtUnixMs <= 0 ||
        SuccessorExpiresAtUnixMs <= SuccessorSentAtUnixMs ||
        ReleaseProfile switch
        {
            RuntimeUnlockInitialActivationOfferEnvelopeRefreshReleaseProfile
                .RegularV4Closure =>
                ClosureGeneration <= 0 ||
                ReceiveRecoveryProof is not null ||
                FallbackCheckpointBinding is not null,
            RuntimeUnlockInitialActivationOfferEnvelopeRefreshReleaseProfile
                .PostTunaFallbackCheckpointProof =>
                ClosureGeneration != 0 ||
                ReceiveRecoveryProof is not { IsDefault: false } proof ||
                proof.Authority != ReceiveRecoveryAuthority ||
                FallbackCheckpointBinding is not
                    { IsDefault: false } binding ||
                binding.ParentAuthorityId !=
                    ReceiveRecoveryAuthority.AuthorityId ||
                binding.ParentOperationGeneration !=
                    ReceiveRecoveryAuthority.OperationGeneration,
            _ => true,
        };
}

internal sealed record
    RuntimeUnlockInitialActivationOfferEnvelopeRefreshReceipt(
        RuntimeUnlockInitialActivationOfferEnvelopeRefreshCommand Command,
        string SuccessorEnvelopeFingerprint,
        long SecureSequence,
        string MessageId)
{
    public bool IsDefault =>
        Command.IsDefault ||
        string.IsNullOrWhiteSpace(SuccessorEnvelopeFingerprint) ||
        string.Equals(
            SuccessorEnvelopeFingerprint,
            Command.HoldRequest.ControlEnvelopeFingerprint,
            StringComparison.Ordinal) ||
        SecureSequence <= 0 ||
        string.IsNullOrWhiteSpace(MessageId);
}

internal enum RuntimeUnlockInitialActivationOfferEnvelopeRefreshStatus
{
    Accepted = 0,
    Idempotent = 1,
    Rejected = 2,
    Invalidated = 3,
}

internal readonly record struct
    RuntimeUnlockInitialActivationOfferEnvelopeRefreshDecision(
        RuntimeUnlockInitialActivationOfferEnvelopeRefreshReceipt Receipt,
        RuntimeUnlockInitialActivationOfferEnvelopeRefreshStatus Status,
        string Reason)
{
    public bool IsDefault =>
        Receipt.IsDefault || string.IsNullOrWhiteSpace(Reason);

    public bool Accepted =>
        Status is
            RuntimeUnlockInitialActivationOfferEnvelopeRefreshStatus.Accepted or
            RuntimeUnlockInitialActivationOfferEnvelopeRefreshStatus.Idempotent;
}

internal sealed class
    RuntimeUnlockInitialActivationOfferEnvelopeRefreshPreparedEventArgs(
        RuntimeUnlockInitialActivationOfferEnvelopeRefreshReceipt receipt)
    : EventArgs
{
    public RuntimeUnlockInitialActivationOfferEnvelopeRefreshReceipt Receipt
    {
        get;
    } = receipt;

    public RuntimeUnlockInitialActivationOfferEnvelopeRefreshDecision? Decision
    {
        get;
        set;
    }
}

internal sealed class
    RuntimeUnlockInitialActivationReceiveRecoveryHoldRequestedEventArgs(
        RuntimeUnlockInitialActivationReceiveRecoveryHoldRequest request)
    : EventArgs
{
    public RuntimeUnlockInitialActivationReceiveRecoveryHoldRequest Request
    {
        get;
    } = request;

    public RuntimeUnlockInitialActivationReceiveRecoveryHoldDecision? Decision
    {
        get;
        set;
    }
}

internal enum RuntimeUnlockInitialActivationReceiveRecoveryHoldInvalidationKind
{
    Unspecified = 0,
    GenericReset = 1,
    UserCancellation = 2,
    PeerCancellation = 3,
}

internal readonly record struct
    RuntimeUnlockInitialActivationReceiveRecoveryHoldInvalidationRequest(
        RuntimeUnlockInitialActivationReceiveRecoveryHoldRequest HoldRequest,
        RuntimeUnlockInitialActivationReceiveRecoveryHoldInvalidationKind Kind,
        string Reason)
{
    public bool IsDefault =>
        HoldRequest.IsDefault ||
        Kind is not (
            RuntimeUnlockInitialActivationReceiveRecoveryHoldInvalidationKind
                .GenericReset or
            RuntimeUnlockInitialActivationReceiveRecoveryHoldInvalidationKind
                .UserCancellation or
            RuntimeUnlockInitialActivationReceiveRecoveryHoldInvalidationKind
                .PeerCancellation) ||
        string.IsNullOrWhiteSpace(Reason);
}

internal sealed class
    RuntimeUnlockInitialActivationReceiveRecoveryHoldInvalidationRequestedEventArgs(
        RuntimeUnlockInitialActivationReceiveRecoveryHoldInvalidationRequest
            request)
    : EventArgs
{
    public RuntimeUnlockInitialActivationReceiveRecoveryHoldInvalidationRequest
        Request
    {
        get;
    } = request;

    public RuntimeUnlockInitialActivationReceiveRecoveryHoldDecision? Decision
    {
        get;
        set;
    }
}

internal interface IRuntimeUnlockInitialActivationReceiveRecoveryHoldBridge
{
    event EventHandler<
        RuntimeUnlockInitialActivationReceiveRecoveryHoldRequestedEventArgs>?
        RuntimeUnlockInitialActivationReceiveRecoveryHoldRequested;

    event EventHandler<
        RuntimeUnlockInitialActivationReceiveRecoveryHoldInvalidationRequestedEventArgs>?
        RuntimeUnlockInitialActivationReceiveRecoveryHoldInvalidationRequested;

    event EventHandler<
        RuntimeUnlockInitialActivationOfferEnvelopeRefreshPreparedEventArgs>?
        RuntimeUnlockInitialActivationOfferEnvelopeRefreshPrepared;

    bool ObserveRuntimeUnlockInitialActivationReceiveRecoveryHoldDecision(
        RuntimeUnlockInitialActivationReceiveRecoveryHoldDecision decision);

    bool ObserveRuntimeUnlockInitialActivationReceiveRecoveryHoldDecision(
        RuntimeUnlockInitialActivationReceiveRecoveryHoldDecision decision,
        RuntimeUnlockInitialActivationReceiveRecoveryFallbackCheckpointBinding
            fallbackCheckpointBinding)
        => ObserveRuntimeUnlockInitialActivationReceiveRecoveryHoldDecision(
            decision);
}

public interface IFileTransferSessionContextProvider
{
    string? CurrentFileTransferSessionId { get; }
}

public interface IFileTransferSignalingTransport
{
    event EventHandler<FileTransferOfferReceivedEventArgs>? FileTransferOfferReceived;
    event EventHandler<FileTransferAcceptReceivedEventArgs>? FileTransferAcceptReceived;
    event EventHandler<FileTransferDeclineReceivedEventArgs>? FileTransferDeclineReceived;
    event EventHandler<FileTransferSessionOpenReceivedEventArgs>? FileTransferSessionOpenReceived;
    event EventHandler<FileTransferCancelReceivedEventArgs>? FileTransferCancelReceived;
    event EventHandler<FileTransferErrorReceivedEventArgs>? FileTransferErrorReceived;
    event EventHandler<FileTransferCompleteReceivedEventArgs>? FileTransferCompleteReceived;
    event EventHandler<FileTransferPauseControlReceivedEventArgs>? FileTransferPauseControlReceived;
    event EventHandler<FileTransferHeartbeatReceivedEventArgs>? FileTransferHeartbeatReceived;
    event EventHandler<FileTransferTransportEpochReceivedEventArgs>? FileTransferTransportEpochReceived;
    event EventHandler<FileTransferTransportProbeReceivedEventArgs>? FileTransferTransportProbeReceived;
    event EventHandler<FileTransferRepairProofReceivedEventArgs>? FileTransferRepairProofReceived;
    event EventHandler<FileTransferFallbackTailYieldProofReceivedEventArgs>? FileTransferFallbackTailYieldProofReceived;

    Task SendFileTransferOfferAsync(FileTransferOfferV2 message, CancellationToken ct);
    Task SendFileTransferAcceptAsync(FileTransferAcceptV1 message, CancellationToken ct);
    Task SendFileTransferDeclineAsync(FileTransferDeclineV1 message, CancellationToken ct);
    Task SendFileTransferSessionOpenAsync(FileTransferSessionOpenV2 message, CancellationToken ct);
    Task SendFileTransferCancelAsync(FileTransferCancelV1 message, CancellationToken ct);
    Task SendFileTransferErrorAsync(FileTransferErrorV1 message, CancellationToken ct);
    Task SendFileTransferCompleteAsync(FileTransferCompleteV1 message, CancellationToken ct);
    Task SendFileTransferPauseControlAsync(FileTransferPauseControlV6 message, CancellationToken ct);
    Task SendFileTransferHeartbeatAsync(FileTransferHeartbeatV6 message, CancellationToken ct);
    Task SendFileTransferTransportEpochAsync(FileTransferTransportEpochV6 message, CancellationToken ct);
    Task SendFileTransferTransportProbeAsync(FileTransferTransportProbeV6 message, CancellationToken ct);
    Task SendFileTransferRepairProofAsync(FileTransferRepairProofV6 message, CancellationToken ct);
    Task<IFileTransferDataSession> OpenFileTransferDataSessionAsync(string sessionId, string transferId, CancellationToken ct);
}

public enum FileTransferControlPlaneKind
{
    Unknown = 0,
    RouteEpoch = 1,
    FallbackCheckpointRequest = 2,
    FallbackCheckpointProof = 3,
    ReceiverState = 4,
    FrontierRequest = 5,
    RuntimeUnlockOffer = 6,
    RuntimeUnlockAnswer = 7,
    RuntimeUnlockAnswerAck = 8,
    TransferCancel = 9,
    SessionEnd = 10,
    LivenessProof = 11,
    FallbackTailYield = 12,
}

public sealed record FileTransferControlPlaneDeliveryRequest(
    FileTransferControlPlaneKind Kind,
    FileTransferDataFrame Frame,
    string Reason)
{
    public FileTransferAuthorityId AuthorityId { get; init; }

    public int AuthoritySchema { get; init; }

    public int ControlPlaneGeneration { get; init; }

    public FileTransferDirection? Direction { get; init; }

    public string? RouteToken { get; init; }

    public int ProtocolVersion { get; init; }

    public int LiveRouteEpoch { get; init; }

    public int TransferLegGeneration { get; init; }

    public int BridgeRecoveryGeneration { get; init; }

    public long TransportEpoch { get; init; }

    public string? CheckpointRequestId { get; init; }

    public bool PeerVisibleRequired { get; init; } = true;

    public bool IgnoreCallerCancellation { get; init; }

    public int PeerCopyAttempts { get; init; } = 1;

    public TimeSpan? CopyTimeout { get; init; }

    public Func<bool>? PreTransportDeliveryValidator { get; init; }
}

public sealed record FileTransferControlPlaneDeliveryResult(
    FileTransferControlPlaneKind Kind,
    string TransferId,
    string SessionId,
    string MessageId,
    bool ControlQueue,
    bool ControlAck,
    bool ControlCopy,
    bool BulkCopy,
    bool PeerVisibleAny,
    bool AcceptedAny,
    string ControlQueueErrorName,
    string ControlAckErrorName,
    string ControlCopyErrorName,
    string BulkCopyErrorName);

public interface IFileTransferControlPlaneDeliveryTransport
{
    Task<FileTransferControlPlaneDeliveryResult> SendFileTransferControlPlaneFrameAsync(
        FileTransferControlPlaneDeliveryRequest request,
        CancellationToken ct);
}

public sealed record FileTransferReceivedDataFrame(
    FileTransferDataFrame Frame,
    FileTransferTransportKind TransportKind,
    string Lane,
    DateTimeOffset ReceivedUtc);

public interface IFileTransferDataSession : IDisposable
{
    string SessionId { get; }

    string TransferId { get; }

    bool IsAvailable { get; }

    event EventHandler<FileTransferDataSessionAvailabilityChangedEventArgs>? AvailabilityChanged;

    ValueTask<FileTransferDataFrame> ReceiveAsync(CancellationToken ct);

    ValueTask<FileTransferReceivedDataFrame> ReceiveWithMetadataAsync(CancellationToken ct);

    Task SendAsync(FileTransferDataFrame frame, CancellationToken ct);
}

public interface IFileTransferDataPathAuthoritySession
{
    Task SendAsync(FileTransferDataFrame frame, FileTransferDataPathSendAuthority authority, CancellationToken ct);
}

internal sealed record FileTransferPhysicalSendSubmission(
    Task DeliveryTask,
    Task PhysicalCompletionTask);

internal interface IFileTransferPhysicalSendSubmissionSession
{
    FileTransferPhysicalSendSubmission SubmitPhysicalSend(
        FileTransferDataFrame frame,
        FileTransferDataPathSendAuthority? authority,
        CancellationToken ct);
}

public sealed class FileTransferRuntimeUnlockAuthorityFrameReceivedEventArgs : EventArgs
{
    public FileTransferRuntimeUnlockAuthorityFrameReceivedEventArgs(FileTransferReceivedDataFrame receivedFrame)
        => ReceivedFrame = receivedFrame ?? throw new ArgumentNullException(nameof(receivedFrame));

    public FileTransferReceivedDataFrame ReceivedFrame { get; }
}

public interface IFileTransferRuntimeUnlockAuthorityFrameReceiver
{
    event EventHandler<FileTransferRuntimeUnlockAuthorityFrameReceivedEventArgs>? RuntimeUnlockAuthorityFrameReceived;
}

public interface IFileTransferRuntimeUnlockPreCommitProbeAckAuthorizer
{
    void AuthorizeRuntimeUnlockPreCommitProbeAck(
        FileTransferRuntimeUnlockPreCommitProbeFrame probe,
        FileTransferTransportKind receivedTransportKind);

    void AuthorizeRuntimeUnlockPreCommitProbeAck(
        FileTransferRuntimeUnlockPreCommitProbeAckFrame ack,
        FileTransferTransportKind receivedTransportKind);
}

public interface IFileTransferRuntimeUnlockTunaProbeSender
{
    Task SendRuntimeUnlockTunaPreCommitProbeAsync(
        FileTransferRuntimeUnlockPreCommitProbeFrame probe,
        CancellationToken ct);

    Task SendRuntimeUnlockTunaPreCommitProbeAckAsync(
        FileTransferRuntimeUnlockPreCommitProbeAckFrame ack,
        CancellationToken ct);
}

public sealed class FileTransferDataSessionAvailabilityChangedEventArgs : EventArgs
{
    public FileTransferDataSessionAvailabilityChangedEventArgs(
        bool isAvailable,
        string reason,
        bool requiresResumeRequest,
        FileTransferTransportHandoffKind handoffKind = FileTransferTransportHandoffKind.None,
        FileTransferTransportKind targetTransport = FileTransferTransportKind.Unknown)
        : this(
            isAvailable,
            reason,
            requiresResumeRequest,
            handoffKind,
            targetTransport,
            runtimeUnlockPreCommitProbeSnapshot: null)
    {
    }

    internal FileTransferDataSessionAvailabilityChangedEventArgs(
        bool isAvailable,
        string reason,
        bool requiresResumeRequest,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport,
        RuntimeUnlockRouteCommitSnapshot? runtimeUnlockPreCommitProbeSnapshot,
        PostTunaFallbackHandoffEvidence? postTunaFallbackHandoffEvidence = null,
        PostTunaFallbackAvailabilityProofEvidence? postTunaFallbackProofBoundary = null)
    {
        IsAvailable = isAvailable;
        Reason = string.IsNullOrWhiteSpace(reason) ? "transport_state_changed" : reason.Trim();
        RequiresResumeRequest = requiresResumeRequest;
        HandoffKind = handoffKind;
        TargetTransport = targetTransport;
        RuntimeUnlockPreCommitProbeSnapshot = runtimeUnlockPreCommitProbeSnapshot;
        PostTunaFallbackHandoffEvidence = postTunaFallbackHandoffEvidence;
        PostTunaFallbackProofBoundary = postTunaFallbackProofBoundary;
    }

    public bool IsAvailable { get; }

    public string Reason { get; }

    public bool RequiresResumeRequest { get; }

    public FileTransferTransportHandoffKind HandoffKind { get; }

    public FileTransferTransportKind TargetTransport { get; }

    internal RuntimeUnlockRouteCommitSnapshot? RuntimeUnlockPreCommitProbeSnapshot { get; }

    internal RuntimeUnlockResponderPeerProbeRouteTransitionObservation?
        RuntimeUnlockResponderPeerProbeRouteTransitionObservation =>
        RuntimeUnlockPreCommitProbeSnapshot
            ?.ResponderPeerProbeRouteTransitionObservation;

    internal PostTunaFallbackHandoffEvidence? PostTunaFallbackHandoffEvidence { get; }

    internal PostTunaFallbackAvailabilityProofEvidence? PostTunaFallbackProofBoundary { get; }
}

public sealed class FileTransferOfferReceivedEventArgs : EventArgs
{
    public FileTransferOfferReceivedEventArgs(FileTransferOfferV2 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public FileTransferOfferV2 Message { get; }

    public string? PeerId { get; }
}

public sealed class FileTransferAcceptReceivedEventArgs : EventArgs
{
    public FileTransferAcceptReceivedEventArgs(FileTransferAcceptV1 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public FileTransferAcceptV1 Message { get; }

    public string? PeerId { get; }
}

public sealed class FileTransferDeclineReceivedEventArgs : EventArgs
{
    public FileTransferDeclineReceivedEventArgs(FileTransferDeclineV1 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public FileTransferDeclineV1 Message { get; }

    public string? PeerId { get; }
}

public sealed class FileTransferCancelReceivedEventArgs : EventArgs
{
    public FileTransferCancelReceivedEventArgs(FileTransferCancelV1 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public FileTransferCancelV1 Message { get; }

    public string? PeerId { get; }
}

public sealed class FileTransferSessionOpenReceivedEventArgs : EventArgs
{
    public FileTransferSessionOpenReceivedEventArgs(FileTransferSessionOpenV2 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public FileTransferSessionOpenV2 Message { get; }

    public string? PeerId { get; }
}

public sealed class FileTransferErrorReceivedEventArgs : EventArgs
{
    public FileTransferErrorReceivedEventArgs(FileTransferErrorV1 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public FileTransferErrorV1 Message { get; }

    public string? PeerId { get; }
}

public sealed class FileTransferCompleteReceivedEventArgs : EventArgs
{
    public FileTransferCompleteReceivedEventArgs(FileTransferCompleteV1 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public FileTransferCompleteV1 Message { get; }

    public string? PeerId { get; }
}

public sealed class FileTransferPauseControlReceivedEventArgs : EventArgs
{
    public FileTransferPauseControlReceivedEventArgs(FileTransferPauseControlV6 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public FileTransferPauseControlV6 Message { get; }

    public string? PeerId { get; }
}

public sealed class FileTransferHeartbeatReceivedEventArgs : EventArgs
{
    public FileTransferHeartbeatReceivedEventArgs(FileTransferHeartbeatV6 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public FileTransferHeartbeatV6 Message { get; }

    public string? PeerId { get; }
}

public sealed class FileTransferTransportEpochReceivedEventArgs : EventArgs
{
    public FileTransferTransportEpochReceivedEventArgs(FileTransferTransportEpochV6 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public FileTransferTransportEpochV6 Message { get; }

    public string? PeerId { get; }
}

public sealed class FileTransferTransportProbeReceivedEventArgs : EventArgs
{
    public FileTransferTransportProbeReceivedEventArgs(FileTransferTransportProbeV6 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public FileTransferTransportProbeV6 Message { get; }

    public string? PeerId { get; }
}

public sealed class FileTransferRepairProofReceivedEventArgs : EventArgs
{
    public FileTransferRepairProofReceivedEventArgs(FileTransferRepairProofV6 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public FileTransferRepairProofV6 Message { get; }

    public string? PeerId { get; }
}

public sealed class FileTransferFallbackTailYieldProofReceivedEventArgs : EventArgs
{
    public FileTransferFallbackTailYieldProofReceivedEventArgs(FileTransferFallbackTailYieldFrameV6 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public FileTransferFallbackTailYieldFrameV6 Message { get; }

    public string? PeerId { get; }
}
