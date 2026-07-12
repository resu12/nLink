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
    public string? RouteToken { get; init; }

    public int ProtocolVersion { get; init; }

    public int LiveRouteEpoch { get; init; }

    public int TransferLegGeneration { get; init; }

    public int BridgeRecoveryGeneration { get; init; }

    public long TransportEpoch { get; init; }

    public string? CheckpointRequestId { get; init; }

    public string? AuthorityReason { get; init; }

    public FileTransferBridgeRecoveryAuthority? BridgeRecoveryAuthority { get; init; }
}

public interface IFileTransferReceiveRecoveryController
{
    void RequestFileTransferReceiveRecovery(FileTransferReceiveRecoveryRequest request);
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
    string? TunaPathLeaseFailureReason = null);

internal interface IRuntimeUnlockRouteCommitProofProvider
{
    void NotifyRuntimeUnlockPathProbeStarted(
        string sessionId,
        string transferId,
        long transportEpoch,
        string probeId,
        FileTransferTransportKind targetTransport,
        string reason);

    void NotifyRuntimeUnlockPathProbeResult(
        string sessionId,
        string transferId,
        long transportEpoch,
        string probeId,
        FileTransferTransportKind targetTransport,
        bool acked,
        string reason);

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
    public bool IsDefault => string.IsNullOrWhiteSpace(SessionId) ||
                             string.IsNullOrWhiteSpace(TransferId) ||
                             SourcePublicationAuthority is null ||
                             RecoveryContractGeneration <= 0 ||
                             DispatchAttemptGeneration <= 0;
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
    public bool IsDefault => AuthorityId.IsDefault ||
                             RuntimeOwner.IsDefault ||
                             SourcePublicationAuthority.IsDefault ||
                             RecoveryContractGeneration <= 0 ||
                             DispatchAttemptGeneration <= 0 ||
                             AuthorityGeneration <= 0;
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

    public RuntimeUnlockRetryDispatchContract(RuntimeUnlockRetryDispatchAuthority authority)
    {
        if (authority.IsDefault)
        {
            throw new ArgumentException("Runtime-unlock retry authority must be non-default.", nameof(authority));
        }

        Authority = authority;
        Lifecycle = new FileTransferAuthorityLifecycle<RuntimeUnlockRetryDispatchSettlementOutcome>(
            authority.AuthorityId);
    }

    public RuntimeUnlockRetryDispatchAuthority Authority { get; }

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

    public bool TryCompleteObserved(
        RuntimeUnlockRetryDispatchAttempt attempt,
        string reason)
        => TryCompleteAttempt(
            attempt,
            RuntimeUnlockRetryDispatchSettlementOutcome.DispatchObserved,
            reason,
            dispatched: true);

    public bool TryCompleteMissingDispatch(
        RuntimeUnlockRetryDispatchAttempt attempt,
        string reason)
        => TryCompleteAttempt(
            attempt,
            RuntimeUnlockRetryDispatchSettlementOutcome.MissingDispatch,
            reason,
            dispatched: false);

    public bool TrySettle(
        RuntimeUnlockRetryDispatchSettlementOutcome outcome,
        string reason,
        FileTransferAuthorityId? successorAuthorityId = null)
    {
        lock (aliasGate)
        {
            if (!Lifecycle.TrySettle(outcome, reason, successorAuthorityId))
            {
                return false;
            }

            attemptPhase = RuntimeUnlockRetryDispatchAttemptPhase.Settled;
            return true;
        }
    }

    public bool TrySupersedeBeforeSend(
        string reason,
        FileTransferAuthorityId successorAuthorityId)
    {
        lock (aliasGate)
        {
            if (attemptPhase == RuntimeUnlockRetryDispatchAttemptPhase.SendInFlight ||
                Lifecycle.IsSettled ||
                !Lifecycle.TrySettle(
                    RuntimeUnlockRetryDispatchSettlementOutcome.Superseded,
                    reason,
                    successorAuthorityId))
            {
                return false;
            }

            attemptPhase = RuntimeUnlockRetryDispatchAttemptPhase.Settled;
            return true;
        }
    }

    private bool TryCompleteAttempt(
        RuntimeUnlockRetryDispatchAttempt attempt,
        RuntimeUnlockRetryDispatchSettlementOutcome outcome,
        string reason,
        bool dispatched)
    {
        lock (aliasGate)
        {
            if (attemptPhase != RuntimeUnlockRetryDispatchAttemptPhase.SendInFlight ||
                attempt.AuthorityId != Authority.AuthorityId ||
                offerGeneration != attempt.OfferGeneration ||
                !string.Equals(offerNonce, attempt.OfferNonce, StringComparison.Ordinal))
            {
                return false;
            }

            var completed = dispatched
                ? Lifecycle.TryMarkDispatchedAndSettle(outcome, reason)
                : Lifecycle.TrySettle(outcome, reason);
            if (completed)
            {
                attemptPhase = RuntimeUnlockRetryDispatchAttemptPhase.Settled;
            }

            return completed;
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

internal sealed class RuntimeUnlockRetryDispatchAuthorityRequestedEventArgs(
    RuntimeUnlockRetryDispatchAuthorityRequest request) : EventArgs
{
    public RuntimeUnlockRetryDispatchAuthorityRequest Request { get; } = request;
}

internal interface IRuntimeUnlockRetryDispatchAuthorityBridge
{
    event EventHandler<RuntimeUnlockRetryDispatchAuthorityRequestedEventArgs>?
        RuntimeUnlockRetryDispatchAuthorityRequested;

    bool TryGetPendingRuntimeUnlockRetryDispatchAuthorityRequest(
        string sessionId,
        string transferId,
        FileTransferDirection direction,
        out RuntimeUnlockRetryDispatchAuthorityRequest request);

    bool ObserveRuntimeUnlockRetryDispatchDecision(RuntimeUnlockRetryDispatchDecision decision);
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
        RuntimeUnlockRouteCommitSnapshot? runtimeUnlockPreCommitProbeSnapshot)
    {
        IsAvailable = isAvailable;
        Reason = string.IsNullOrWhiteSpace(reason) ? "transport_state_changed" : reason.Trim();
        RequiresResumeRequest = requiresResumeRequest;
        HandoffKind = handoffKind;
        TargetTransport = targetTransport;
        RuntimeUnlockPreCommitProbeSnapshot = runtimeUnlockPreCommitProbeSnapshot;
    }

    public bool IsAvailable { get; }

    public string Reason { get; }

    public bool RequiresResumeRequest { get; }

    public FileTransferTransportHandoffKind HandoffKind { get; }

    public FileTransferTransportKind TargetTransport { get; }

    internal RuntimeUnlockRouteCommitSnapshot? RuntimeUnlockPreCommitProbeSnapshot { get; }
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
