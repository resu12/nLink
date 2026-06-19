using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;

namespace NLink.Infra.Nkn;

public sealed partial class NknSignalingTransport
{
    private const int TunaSidecarProtocolVersion = NknTunaSidecarCompatibility.AppProtocolVersion;
    private const int AccelerationNegotiationMaxRetryAttempts = 3;
    private const int RuntimeUnlockAccelerationNegotiationMaxRetryAttempts = 8;
    private const int AccelerationEarlyDropMaxRetryAttempts = 1;
    private static readonly TimeSpan AccelerationOfferLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AccelerationOfferAnswerTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RuntimeUnlockAccelerationOfferAnswerTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AccelerationOfferReplayDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AccelerationNegotiationRetryBaseDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HelperPaidOfferHelpeePriorityDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HelperPaidOfferHelpeeIntentGraceDelay = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan AccelerationListenerReadyRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan AccelerationControlDirectSendWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AccelerationControlBulkBypassWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AccelerationAnswerAckTimeout = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan RuntimeUnlockAccelerationAnswerAckTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AccelerationAnswerReplayDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AccelerationAnswerAckReplayDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FileTransferTunaActivationPauseMax = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan FileTransferTunaActivationBridgeRecoveryWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RuntimeUnlockRecoverySoftSettleDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RuntimeUnlockRecoveryContractRetryDeadline = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan RuntimeUnlockRecoveryContractLivenessDeferral = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RuntimeUnlockRecoveryContractStaleNegotiationWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RuntimeUnlockRetryAuthorityDeadline = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RuntimeUnlockRetryAuthorityInFlightSendGrace = TimeSpan.FromSeconds(30);
    // Runtime-unlock cut-through is only useful if peer-visible proof arrives
    // before liveness and GUI route-proof gates expire. The normal answer
    // timeout remains longer after peer receipt is proven.
    private static readonly TimeSpan RuntimeUnlockOfferPeerResponseTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RuntimeUnlockPeerVisibleLifecycleCopyTimeout = TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan RuntimeUnlockRecoveryContractRetryMaxDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RuntimeUnlockRecoveryContractRetryDeadlineSlack = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RuntimeUnlockBrokenStateRetryBudgetTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FileTransferFallbackRecoveryLivenessDeferral = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan FileTransferFallbackRecoveryLivenessMaxDeferral = TimeSpan.FromSeconds(210);
    private static readonly TimeSpan FileTransferRegularV4RecoveryLivenessDeferral = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan FileTransferRegularV4RecoveryLivenessMaxDeferral = TimeSpan.FromSeconds(210);
    private static readonly TimeSpan FileTransferRegularV4BridgeRecoveryStartedTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan RuntimeUnlockRegularV4BridgeRecoveryStartedAuthorityProbeDelay = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan RuntimeUnlockRegularV4FinalObservedSendProbeWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RuntimeUnlockQueueAcceptedObservedEscapeTtl = TimeSpan.FromSeconds(30);
    private const string RuntimeUnlockCutThroughRetryReason = "runtime_unlock_offer_peer_response_timeout";
    private const string RuntimeUnlockCutThroughRecoveryReason = "tuna_activation_offer_peer_response_timeout";
    private const int RuntimeUnlockCutThroughMaxAttempts = 2;
    private const int RuntimeUnlockBrokenStateMaxFreshRetries = 1;
    private const string RuntimeUnlockPostTunaFallbackFinalProbeReason = "post_tuna_fallback_checkpoint_pending_final_probe";
    private const string RuntimeUnlockRegularV4AuthorityProbeReason = "regular_v4_receive_recovery_soft_settled";
    private static readonly TimeSpan RemotePayerIntentFreshness = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TunaFallbackProofLogWindow = TimeSpan.FromMinutes(1);
    private const long TunaFallbackProofLogEveryFrames = 5000;
    private const int AccelerationOfferReplayAttempts = 12;
    private const int AccelerationAnswerReplayAttempts = 3;
    private const int RuntimeUnlockAccelerationAnswerReplayAttempts = 16;
    private const int AccelerationAnswerAckReplayAttempts = 3;
    private const string AccelerationObservedLaneControlQueue = "control_queue";
    private const string AccelerationObservedLaneControlPriority = "control_priority";
    private const string AccelerationObservedLaneControlToBulkEndpoint = "control_to_bulk_endpoint";
    private const string AccelerationObservedLaneBulkQueueFallback = "bulk_queue_fallback";
    private const string AccelerationObservedLaneControlQueueExplicitObserved = "control_queue_explicit_observed";
    private const int RemoteHelpeePayerIntentUnknown = 0;
    private const int RemoteHelpeePayerIntentWillListen = 1;
    private const int RemoteHelpeePayerIntentDialerOnly = 2;
    [ThreadStatic]
    private static bool handlingTunaAcceleratedInboundMessage;
    internal static TimeSpan? AccelerationOfferAnswerTimeoutOverrideForTests;
    internal static TimeSpan? AccelerationOfferReplayDelayOverrideForTests;
    internal static TimeSpan? AccelerationAnswerReplayDelayOverrideForTests;
    internal static int? AccelerationAnswerReplayAttemptsOverrideForTests;
    internal static TimeSpan? AccelerationAnswerAckTimeoutOverrideForTests;
    internal static TimeSpan? AccelerationControlDirectSendWaitOverrideForTests;
    internal static TimeSpan? AccelerationControlBulkBypassWaitOverrideForTests;
    internal static TimeSpan? FileTransferTunaActivationBridgeRecoveryWaitOverrideForTests;
    internal static TimeSpan? RuntimeUnlockRegularV4BridgeRecoveryStartedAuthorityProbeDelayOverrideForTests;
    internal static TimeSpan? RuntimeUnlockRecoverySoftSettleDelayOverrideForTests;
    internal static TimeSpan? RuntimeUnlockRecoveryContractStaleNegotiationWindowOverrideForTests;
    internal static TimeSpan? RuntimeUnlockRetryAuthorityDeadlineOverrideForTests;
    internal static TimeSpan? RuntimeUnlockRetryAuthorityPeerProofFreshnessOverrideForTests;
    internal static TimeSpan? RuntimeUnlockOfferPeerResponseTimeoutOverrideForTests;
    internal static TimeSpan? HelperPaidOfferHelpeePriorityDelayOverrideForTests;
    internal static TimeSpan? HelperPaidOfferHelpeeIntentGraceDelayOverrideForTests;
    internal static Func<NknSignalingTransport, bool>? RuntimeUnlockOfferQueueAcceptedPressureOverrideForTests;
    internal static Func<NknSignalingTransport, string?>? RuntimeUnlockOfferObservedSendBlockerReasonOverrideForTests;
    internal static Func<NknSignalingTransport, string?>? RuntimeUnlockRegularV4ReceiveRecoveryBlockerOverrideForTests;
    internal static Func<NknSignalingTransport, string, string?, bool>? RuntimeUnlockOfferSendRecoveryRequestOverrideForTests;
    private readonly object accelerationGate = new();
    private readonly object accelerationBridgeRecoveryGate = new();
    private string? outboundAccelerationOfferNonce;
    private string? outboundAccelerationOfferTrigger;
    private long outboundAccelerationOfferGeneration;
    private string? accelerationSessionId;
    private NknAccelerationLaneKind accelerationNegotiatedLanes;
    private int accelerationNegotiationScheduled;
    private int accelerationNegotiationRetryAttempts;
    private int pendingRuntimeUnlockAccelerationNegotiation;
    private int accelerationEarlyDropRetryAttempts;
    private int helperPaidOfferPriorityDelayConsumed;
    private int remoteHelpeeAccelerationOfferObserved;
    private int remoteHelpeePayerIntentState;
    private long remoteHelpeePayerIntentObservedUtcMs;
    private long accelerationPayerDecisionId;
    private long outboundAccelerationOfferPayerDecisionId;
    private long remoteAccelerationPayerDecisionId;
    private string? retiredAccelerationOfferSessionId;
    private string? retiredAccelerationOfferNonce;
    private string? retiredAccelerationOfferTrigger;
    private long retiredAccelerationOfferPayerDecisionId;
    private long retiredAccelerationOfferExpiresUtcMs;
    private long fileTransferTunaActivationPauseGeneration;
    private string? fileTransferTunaActivationPauseSessionId;
    private TaskCompletionSource<bool>? fileTransferTunaActivationBridgeRecoverySettledTcs;
    private long fileTransferTunaActivationBridgeRecoveryStartedTick;
    private long fileTransferTunaActivationBridgeRecoverySettledTick;
    private int fileTransferTunaActivationBridgeRecoveryActive;
    private long runtimeUnlockRecoveryContractNextGeneration;
    private RuntimeUnlockRecoveryRetryState? runtimeUnlockRecoveryRetryState;
    private long runtimeUnlockTransactionNextGeneration;
    private RuntimeUnlockTransactionSnapshot runtimeUnlockTransactionState = RuntimeUnlockTransactionSnapshot.Idle;
    private long runtimeUnlockTunaPathLeaseNextGeneration;
    private RuntimeUnlockTunaPathLeaseSnapshot runtimeUnlockTunaPathLeaseState = RuntimeUnlockTunaPathLeaseSnapshot.None;
    private string? runtimeUnlockBrokenStateRetryBudgetSessionId;
    private string? runtimeUnlockBrokenStateRetryBudgetFamily;
    private long runtimeUnlockBrokenStateRetryBudgetStartedUtcMs;
    private int runtimeUnlockBrokenStateFreshRetriesUsed;
    private FileTransferFallbackLegAuthorityState? fileTransferFallbackLegAuthorityState;
    private FileTransferRegularV4RecoveryLivenessState? fileTransferRegularV4RecoveryLivenessState;
    private string? pendingAccelerationAnswerAckSessionId;
    private string? pendingAccelerationAnswerAckNonce;
    private string? pendingAccelerationAnswerAckTrigger;
    private NknAccelerationLaneKind pendingAccelerationAnswerAckLanes;
    private long pendingAccelerationAnswerAckPayerDecisionId;
    private long pendingAccelerationAnswerAckGeneration;
    private RuntimeUnlockOfferProofState? runtimeUnlockOfferProofState;
    private long runtimeUnlockQueueAcceptedObservedEscapeGeneration;
    private long runtimeUnlockQueueAcceptedObservedEscapePayerDecisionId;
    private long runtimeUnlockQueueAcceptedObservedEscapeTick;
    private string? runtimeUnlockQueueAcceptedObservedEscapeReason;
    private int transportAccelerationActivePublished;
    private string transportAccelerationStatusReason = "inactive";
    private string? accelerationUserStoppedSessionId;
    private long accelerationUserStoppedUtcMs;
    private string? accelerationPeerUserStoppedSessionId;
    private long accelerationPeerUserStoppedUtcMs;
    private long tunaFallbackProofNextEpoch;
    private TunaFallbackProofState? tunaFallbackProofState;

    private enum TunaFallbackLaneState
    {
        None = 0,
        Pending = 1,
        MediaReady = 2,
        Recovered = 3,
        WaitingForRegularNkn = 4,
    }

    private sealed class TunaFallbackProofState
    {
        public required long Epoch { get; init; }

        public required string SessionId { get; init; }

        public required string Reason { get; init; }

        public required DateTimeOffset StartedUtc { get; init; }

        public required NknAccelerationLaneKind Lanes { get; init; }

        public long ScreenNknFramesSent { get; set; }

        public long ScreenNknFramesReceived { get; set; }

        public long FileNknFramesSent { get; set; }

        public long FileNknFramesReceived { get; set; }

        public long ControlNknMessagesSent { get; set; }

        public TunaFallbackLaneState ScreenState { get; set; }

        public TunaFallbackLaneState FileState { get; set; }

        public V6TransportEpochState? FileV6EpochState { get; set; }

        public long FileV6TransportEpoch { get; set; }

        public long ScreenFramesApplied { get; set; }

        public bool AccelerationUsedAfterFallback { get; set; }

        public Dictionary<string, TunaFallbackProofLogState> LogStates { get; } = new(StringComparer.Ordinal);
    }

    private sealed class TunaFallbackProofLogState
    {
        public long CountSinceLastLog { get; set; }

        public DateTimeOffset LastLoggedUtc { get; set; } = DateTimeOffset.MinValue;
    }

    private sealed class RuntimeUnlockOfferProofState
    {
        public required long Generation { get; init; }

        public required string Nonce { get; init; }

        public required string SessionId { get; init; }

        public required long PayerDecisionId { get; init; }

        public required string Trigger { get; init; }

        public required long CreatedUtcMs { get; init; }

        public bool ObservedSend { get; set; }

        public string? ObservedSendLane { get; set; }

        public bool PeerReceived { get; set; }

        public bool AnswerTimeoutScheduled { get; set; }

        public bool Retired { get; set; }

        public string? RetiredReason { get; set; }

        public string? RetryReason { get; set; }
    }

    private sealed class RuntimeUnlockRecoveryRetryState
    {
        public required long ContractGeneration { get; init; }

        public required long RetiredOfferGeneration { get; init; }

        public long CurrentOfferGeneration { get; set; }

        public required string SessionId { get; init; }

        public string? TransferId { get; init; }

        public required string RetryReason { get; init; }

        public required string RecoveryReason { get; init; }

        public required long CreatedUtcMs { get; init; }

        public long RetryDeadlineUtcMs { get; set; }

        public long LivenessDeferralDeadlineUtcMs { get; set; }

        public SessionRecoveryContractState ContractState { get; set; } = SessionRecoveryContractState.RecoveryPending;

        public bool Settled { get; set; }

        public bool RetryQueued { get; set; }

        public bool RetryDispatching { get; set; }

        public bool RetryDispatched { get; set; }

        public bool RetryObserved { get; set; }

        public bool QueuedBehindActiveNegotiation { get; set; }

        public bool RetryAuthorityPending { get; set; }

        public bool RetryAuthorityGranted { get; set; }

        public bool ObservedSendPending { get; set; }

        public long ObservedSendDeadlineUtcMs { get; set; }

        public string? AuthorizedObservedLane { get; set; }

        public string? AuthorityFailureReason { get; set; }

        public int AuthorityAttempt { get; set; }

        public bool RequiresLocalListenerRetry { get; init; }

        public bool ListenerRearmCompleted { get; set; }

        public bool CutThroughPending { get; set; }

        public bool CutThroughActive { get; set; }

        public bool CutThroughOfferSent { get; set; }

        public bool CutThroughPeerReceived { get; set; }

        public bool CutThroughCompleted { get; set; }

        public int CutThroughAttempt { get; set; }

        public int BrokenStateFreshRetryAttempt { get; set; }

        public int BrokenStateFreshRetryLimit { get; init; } = RuntimeUnlockBrokenStateMaxFreshRetries;

        public bool BrokenStateFinalized { get; set; }

        public string? BrokenStateFailureReason { get; set; }
    }

    private sealed class FileTransferFallbackLegAuthorityState
    {
        public required string SessionId { get; init; }

        public required string TransferId { get; init; }

        public required string Direction { get; init; }

        public required int LegGeneration { get; init; }

        public required string RouteToken { get; init; }

        public required int ProtocolVersion { get; init; }

        public required int LiveRouteEpoch { get; init; }

        public required long TransportEpoch { get; init; }

        public required int BridgeRecoveryGeneration { get; init; }

        public string? CheckpointRequestId { get; init; }

        public required string AuthorityReason { get; init; }

        public required long CreatedUtcMs { get; init; }

        public required long LivenessDeferralDeadlineUtcMs { get; set; }

        public bool BridgeRecoveryRequested { get; set; }

        public bool BridgeRecoveryStarted { get; set; }

        public bool BridgeRecoveryCompleted { get; set; }

        public bool ReceiveProofObserved { get; set; }

        public bool RecoveryExhausted { get; set; }

        public bool BridgeRecoveryEscalated { get; set; }

        public bool Completed { get; set; }
    }

    private sealed class FileTransferRegularV4RecoveryLivenessState
    {
        public required string SessionId { get; init; }

        public required string TransferId { get; init; }

        public required int Generation { get; init; }

        public required string RouteToken { get; init; }

        public required int ProtocolVersion { get; init; }

        public required int LiveRouteEpoch { get; init; }

        public required string AuthorityReason { get; init; }

        public required long CreatedUtcMs { get; init; }

        public required long LivenessDeferralDeadlineUtcMs { get; set; }

        public bool BridgeRecoveryRequested { get; set; }

        public bool BridgeRecoveryStarted { get; set; }

        public long BridgeRecoveryStartedUtcMs { get; set; }

        public bool BridgeRecoveryCompleted { get; set; }

        public bool ReceiveProofObserved { get; set; }

        public bool RecoveryExhausted { get; set; }

        public bool Completed { get; set; }
    }

    private readonly record struct AccelerationControlSendResult(
        bool Succeeded,
        string? ObservedLane,
        bool RecoveryRequested,
        string? RecoveryReason,
        string? RecoverySessionId,
        bool UntrustedProbeSent = false)
    {
        public static AccelerationControlSendResult Failed => new(false, null, false, null, null);

        public static AccelerationControlSendResult Success(string observedLane)
            => new(true, observedLane, false, null, null);

        public static AccelerationControlSendResult UntrustedProbe(string observedLane)
            => new(false, observedLane, false, null, null, true);

        public static AccelerationControlSendResult RecoveryRequestedResult(string recoveryReason, string? recoverySessionId)
            => new(false, null, true, recoveryReason, recoverySessionId);
    }

    private sealed record AccelerationControlSendAttempt(
        Task<AccelerationControlSendResult> Task,
        bool PreferredObservedLane = false);

    private readonly record struct AccelerationValidationResult(
        bool IsHardReject,
        string? Reason,
        NknAccelerationLaneKind AcceptedLanes,
        bool AllowsStaleRemotePayerDecision = false)
    {
        public bool IsValid => Reason is null;

        public static AccelerationValidationResult Valid(
            NknAccelerationLaneKind acceptedLanes,
            bool allowsStaleRemotePayerDecision = false)
            => new(false, null, acceptedLanes, allowsStaleRemotePayerDecision);

        public static AccelerationValidationResult HardReject(string reason)
            => new(true, reason, NknAccelerationLaneKind.None);

        public static AccelerationValidationResult SoftReject(string reason)
            => new(false, reason, NknAccelerationLaneKind.None);
    }

    internal bool IsAccelerationAvailableForTests => IsAccelerationNegotiatedAndHealthy();

    internal bool HasAccelerationLaneForTests => accelerationLane is not null;

    internal bool AccelerationCanOfferListenerForTests
        => accelerationLane is INknTunaAccelerationSession tunaSession && tunaSession.CanOfferListener;

    internal NknAccelerationLaneKind AccelerationNegotiatedLanesForTests
    {
        get
        {
            lock (accelerationGate)
            {
                return accelerationNegotiatedLanes;
            }
        }
    }

    internal NknAccelerationLaneDiagnostics AccelerationDiagnosticsForTests
    {
        get
        {
            var diagnostics = accelerationLane?.GetDiagnosticsSnapshot() ?? NknAccelerationLaneDiagnostics.Empty;
            lock (accelerationGate)
            {
                return diagnostics with { FallbackEpoch = tunaFallbackProofState?.Epoch ?? diagnostics.FallbackEpoch };
            }
        }
    }

    internal FileTransferV6TransportEpochDiagnostics FileTransferV6TransportEpochDiagnosticsForTests
    {
        get
        {
            lock (fileTransferV6TransportEpochGate)
            {
                return new FileTransferV6TransportEpochDiagnostics(
                    observedFileTransferV6TransportEpochStartedCount,
                    observedFileTransferV6NormalToTunaActivationStartedCount,
                    observedFileTransferV6TransportEpochRecoveredCount,
                    observedFileTransferV6NormalToTunaActivationRecoveredCount,
                    observedFileTransferV6TransportEpochWaitingCount,
                    observedFileTransferV6TransportEpochTerminalCount,
                    unresolvedFileTransferV6TransportEpochs.Count);
            }
        }
    }

    internal void SeedRuntimeUnlockOfferCriticalSectionForTests(
        string sessionId,
        string nonce,
        long payerDecisionId,
        long generation,
        bool observedSend = false,
        string? observedLane = null,
        bool peerReceived = false,
        bool answerTimeoutScheduled = false,
        bool preserveRecoveryState = false)
    {
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId)
            ? currentSessionSecurityState.SessionId?.Value ?? string.Empty
            : sessionId.Trim();
        var normalizedNonce = string.IsNullOrWhiteSpace(nonce) ? "test_nonce" : nonce.Trim();
        lock (accelerationGate)
        {
            accelerationSessionId = null;
            accelerationNegotiatedLanes = NknAccelerationLaneKind.None;
            outboundAccelerationOfferNonce = normalizedNonce;
            outboundAccelerationOfferTrigger = "runtime_unlock";
            outboundAccelerationOfferPayerDecisionId = payerDecisionId;
            outboundAccelerationOfferGeneration = generation;
            runtimeUnlockOfferProofState = new RuntimeUnlockOfferProofState
            {
                Generation = generation,
                Nonce = normalizedNonce,
                SessionId = normalizedSessionId,
                PayerDecisionId = payerDecisionId,
                Trigger = "runtime_unlock",
                CreatedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ObservedSend = observedSend,
                ObservedSendLane = observedLane,
                PeerReceived = peerReceived,
                AnswerTimeoutScheduled = answerTimeoutScheduled,
            };
            var leaseGeneration = ++runtimeUnlockTunaPathLeaseNextGeneration;
            var listenerRunId = $"listener-start-{leaseGeneration.ToString(CultureInfo.InvariantCulture)}";
            var lease = RuntimeUnlockTunaPathLease.Start(
                normalizedSessionId,
                transferId: null,
                leaseGeneration,
                listenerRunId,
                payerDecisionId,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            RuntimeUnlockTunaPathLease.TryMarkListenerReady(
                lease,
                normalizedSessionId,
                leaseGeneration,
                listenerRunId,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                out lease);
            runtimeUnlockTunaPathLeaseState = RuntimeUnlockTunaPathLease.BindOffer(
                lease,
                runtimeUnlockTransactionState.TransactionGeneration,
                generation,
                payerDecisionId,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (!preserveRecoveryState)
            {
                runtimeUnlockRecoveryRetryState = null;
            }
        }
    }

    internal (
        bool HasOutboundOffer,
        bool IsRetired,
        string? RetiredReason,
        bool PeerReceived,
        bool RetryArmed,
        bool RetryQueued,
        string? RecoveryReason) RuntimeUnlockOfferStateForTests
    {
        get
        {
            lock (accelerationGate)
            {
                return (
                    !string.IsNullOrWhiteSpace(outboundAccelerationOfferNonce),
                    runtimeUnlockOfferProofState?.Retired ?? false,
                    runtimeUnlockOfferProofState?.RetiredReason,
                    runtimeUnlockOfferProofState?.PeerReceived ?? false,
                    runtimeUnlockRecoveryRetryState is not null,
                    runtimeUnlockRecoveryRetryState?.RetryQueued ?? false,
                    runtimeUnlockRecoveryRetryState?.RecoveryReason);
            }
        }
    }

    internal (
        bool Captured,
        string SessionId,
        long PayerDecisionId,
        long Generation,
        string RetryReason) CaptureUnobservedRuntimeUnlockOfferResetRetryForTests(string reason)
    {
        lock (accelerationGate)
        {
            var captured = TryCaptureUnobservedRuntimeUnlockOfferResetRetryLocked(
                reason,
                out var sessionId,
                out var payerDecisionId,
                out var generation,
                out var retryReason);
            return (captured, sessionId, payerDecisionId, generation, retryReason);
        }
    }

    public void RequestFileTransferReceiveRecovery(FileTransferReceiveRecoveryRequest request)
    {
        var sessionId = string.IsNullOrWhiteSpace(request.SessionId)
            ? currentSessionSecurityState.SessionId?.Value
            : request.SessionId.Trim();
        var transferId = string.IsNullOrWhiteSpace(request.TransferId) ? "(none)" : request.TransferId.Trim();
        var reason = string.IsNullOrWhiteSpace(request.Reason)
            ? "core_filetransfer_receive_recovery"
            : SanitizeLogToken(request.Reason);
        var direction = request.Direction.ToString().ToLowerInvariant();
        var authorityFields = FormatFileTransferFallbackLegAuthorityFields(request);
        var hasFallbackLegAuthority = HasFileTransferFallbackLegAuthority(request);
        var hasRegularV4RecoveryLiveness = HasFileTransferRegularV4RecoveryLiveness(request);
        var bridgeRecoveryReason = ResolveFileTransferReceiveStallRecoveryBridgeReason(
            request,
            reason,
            hasFallbackLegAuthority);
        FileTransferFallbackLegAuthorityState? fallbackAuthority = null;
        FileTransferRegularV4RecoveryLivenessState? regularV4Recovery = null;

        if (hasFallbackLegAuthority &&
            ShouldIgnoreStalePostTunaFallbackLegAuthorityRequest(request, sessionId, transferId, reason))
        {
            return;
        }

        if (client is not RealNknClientAdapter realClient)
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=filetransfer_v6_bridge_receive_recovery_request_unsupported; session_id={SanitizeLogToken(sessionId ?? "none")}; transfer_id={SanitizeLogToken(transferId)}; direction={direction}; reason={reason}{authorityFields}");
            return;
        }

        if (hasFallbackLegAuthority)
        {
            fallbackAuthority = MarkFileTransferFallbackLegAuthorityStarted(request, sessionId, transferId, reason);
            realClient.MarkActiveFileTransferPostTunaFallbackLegAuthority(
                transferId,
                sessionId ?? "none",
                request.TransferLegGeneration,
                request.RouteToken ?? "post_tuna_fallback_v6",
                request.ProtocolVersion,
                request.LiveRouteEpoch,
                request.TransportEpoch,
                request.BridgeRecoveryGeneration,
                request.CheckpointRequestId,
                request.AuthorityReason ?? reason);
        }

        var accepted = realClient.RequestFileTransferReceiveStallRecovery(bridgeRecoveryReason);
        if (accepted && fallbackAuthority is not null)
        {
            MarkFileTransferFallbackLegAuthorityBridgeRecoveryRequested(fallbackAuthority, bridgeRecoveryReason);
        }
        else if (accepted && hasRegularV4RecoveryLiveness)
        {
            regularV4Recovery = MarkFileTransferRegularV4RecoveryLivenessStarted(request, sessionId, transferId, reason);
            MarkFileTransferRegularV4RecoveryLivenessBridgeRecoveryRequested(regularV4Recovery, bridgeRecoveryReason);
        }

        LocalOperationalLog.Warn(
            "NKN.Tuna",
            $"event=filetransfer_v6_bridge_receive_recovery_requested; session_id={SanitizeLogToken(sessionId ?? "none")}; transfer_id={SanitizeLogToken(transferId)}; direction={direction}; reason={reason}; bridge_recovery_reason={bridgeRecoveryReason}; accepted={(accepted ? 1 : 0)}{authorityFields}");
        if (!accepted)
        {
            return;
        }

        if (IsSessionLivenessReceiveRecoveryReason(reason))
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=filetransfer_session_liveness_receive_recovery_availability_preserved; session_id={SanitizeLogToken(sessionId ?? "none")}; transfer_id={SanitizeLogToken(transferId)}; direction={direction}; reason={reason}; trigger=core_receive_recovery_requested");
            return;
        }

        if (ShouldUseFileTransferV6EpochForRegularNknRecovery(sessionId))
        {
            MarkFileTransferFallbackNknProofPending(
                reason,
                sessionId,
                NknAccelerationLaneKind.File,
                request);
            SetFileTransferDataSessionsAvailability(
                isAvailable: false,
                reason: reason,
                requiresResumeRequest: true,
                handoffKind: FileTransferTransportHandoffKind.RegularNknRecovery,
                targetTransport: FileTransferTransportKind.RegularNkn);
            ScheduleFileTransferFallbackNknProbeIfPending("core_receive_recovery_requested");
            return;
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=filetransfer_regular_nkn_receive_recovery_no_epoch; session_id={SanitizeLogToken(sessionId ?? "none")}; transfer_id={SanitizeLogToken(transferId)}; direction={direction}; reason={reason}; trigger=core_receive_recovery_requested");
        SetFileTransferDataSessionsAvailability(
            isAvailable: false,
            reason: reason,
            requiresResumeRequest: false,
            handoffKind: FileTransferTransportHandoffKind.None,
            targetTransport: FileTransferTransportKind.RegularNkn);
    }

    private static bool HasFileTransferFallbackLegAuthority(FileTransferReceiveRecoveryRequest request)
        => request.TransferLegGeneration > 0 &&
           request.ProtocolVersion == FileTransferProtocol.ProtocolVersionV6 &&
           string.Equals(request.RouteToken, "post_tuna_fallback_v6", StringComparison.Ordinal);

    private static bool HasFileTransferRegularV4RecoveryLiveness(FileTransferReceiveRecoveryRequest request)
        => request.ProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
           string.Equals(request.RouteToken, FileTransferRouteResolver.RegularNknV4FastToken, StringComparison.Ordinal);

    private static string ResolveFileTransferReceiveStallRecoveryBridgeReason(
        FileTransferReceiveRecoveryRequest request,
        string reason,
        bool hasFallbackLegAuthority)
    {
        if (!hasFallbackLegAuthority)
        {
            return reason;
        }

        if (!string.IsNullOrWhiteSpace(request.AuthorityReason) &&
            IsPostTunaFallbackReceiveStallRecoveryReason(request.AuthorityReason))
        {
            return SanitizeLogToken(request.AuthorityReason);
        }

        if (IsPostTunaFallbackReceiveStallRecoveryReason(reason))
        {
            return reason;
        }

        return "post_tuna_fallback_receive_recovery";
    }

    private static string FormatFileTransferFallbackLegAuthorityFields(FileTransferReceiveRecoveryRequest request)
    {
        if (!HasFileTransferFallbackLegAuthority(request))
        {
            if (!HasFileTransferRegularV4RecoveryLiveness(request))
            {
                return string.Empty;
            }

            return
                $"; route={SanitizeLogToken(request.RouteToken ?? FileTransferRouteResolver.RegularNknV4FastToken)}" +
                $"; protocol_version={request.ProtocolVersion}" +
                $"; live_route_epoch={request.LiveRouteEpoch}" +
                $"; authority_reason={SanitizeLogToken(request.AuthorityReason ?? request.Reason)}";
        }

        return
            $"; route={SanitizeLogToken(request.RouteToken ?? "post_tuna_fallback_v6")}" +
            $"; protocol_version={request.ProtocolVersion}" +
            $"; live_route_epoch={request.LiveRouteEpoch}" +
            $"; leg_generation={request.TransferLegGeneration}" +
            $"; bridge_recovery_generation={request.BridgeRecoveryGeneration}" +
            $"; transport_epoch={request.TransportEpoch}" +
            $"; checkpoint_request_id={SanitizeLogToken(request.CheckpointRequestId ?? "none")}" +
            $"; authority_reason={SanitizeLogToken(request.AuthorityReason ?? "none")}";
    }

    private FileTransferFallbackLegAuthorityState MarkFileTransferFallbackLegAuthorityStarted(
        FileTransferReceiveRecoveryRequest request,
        string? sessionId,
        string transferId,
        string reason)
    {
        var normalizedSessionId = SanitizeLogToken(sessionId ?? "none");
        var normalizedTransferId = SanitizeLogToken(transferId);
        var normalizedRoute = SanitizeLogToken(request.RouteToken ?? "post_tuna_fallback_v6");
        var normalizedAuthorityReason = SanitizeLogToken(request.AuthorityReason ?? reason);
        FileTransferFallbackLegAuthorityState state;
        bool started = false;
        lock (fileTransferFallbackProofGate)
        {
            var existing = fileTransferFallbackLegAuthorityState;
            if (existing is not null &&
                !existing.Completed &&
                string.Equals(existing.SessionId, normalizedSessionId, StringComparison.Ordinal) &&
                string.Equals(existing.TransferId, normalizedTransferId, StringComparison.Ordinal) &&
                existing.LegGeneration == request.TransferLegGeneration)
            {
                state = existing;
            }
            else
            {
                var now = DateTimeOffset.UtcNow;
                state = new FileTransferFallbackLegAuthorityState
                {
                    SessionId = normalizedSessionId,
                    TransferId = normalizedTransferId,
                    Direction = request.Direction.ToString().ToLowerInvariant(),
                    LegGeneration = request.TransferLegGeneration,
                    RouteToken = normalizedRoute,
                    ProtocolVersion = request.ProtocolVersion,
                    LiveRouteEpoch = request.LiveRouteEpoch,
                    TransportEpoch = request.TransportEpoch,
                    BridgeRecoveryGeneration = request.BridgeRecoveryGeneration,
                    CheckpointRequestId = SanitizeLogToken(request.CheckpointRequestId ?? "none"),
                    AuthorityReason = normalizedAuthorityReason,
                    CreatedUtcMs = now.ToUnixTimeMilliseconds(),
                    LivenessDeferralDeadlineUtcMs = now
                        .Add(FileTransferFallbackRecoveryLivenessDeferral)
                        .ToUnixTimeMilliseconds(),
                };
                fileTransferFallbackLegAuthorityState = state;
                started = true;
            }
        }

        if (started)
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=filetransfer_fallback_leg_authority_started; direction={state.Direction}; session_id={state.SessionId}; transfer_id={state.TransferId}; leg_generation={state.LegGeneration}; route={state.RouteToken}; protocol_version={state.ProtocolVersion}; live_route_epoch={state.LiveRouteEpoch}; transport_epoch={state.TransportEpoch}; bridge_recovery_generation={state.BridgeRecoveryGeneration}; checkpoint_request_id={SanitizeLogToken(state.CheckpointRequestId ?? "none")}; authority_reason={state.AuthorityReason}; reason={reason}");
        }

        return state;
    }

    private void MarkFileTransferFallbackLegAuthorityBridgeRecoveryRequested(
        FileTransferFallbackLegAuthorityState state,
        string reason)
    {
        var shouldLog = false;
        lock (fileTransferFallbackProofGate)
        {
            if (ReferenceEquals(fileTransferFallbackLegAuthorityState, state) &&
                !state.BridgeRecoveryRequested)
            {
                state.BridgeRecoveryRequested = true;
                RefreshFileTransferFallbackLegAuthorityLivenessDeadlineUnsafe(state);
                shouldLog = true;
            }
        }

        if (!shouldLog)
        {
            return;
        }

        LocalOperationalLog.Warn(
            "NKN.Tuna",
            $"event=filetransfer_fallback_leg_authority_bridge_recovery_requested; direction={state.Direction}; session_id={state.SessionId}; transfer_id={state.TransferId}; leg_generation={state.LegGeneration}; route={state.RouteToken}; protocol_version={state.ProtocolVersion}; live_route_epoch={state.LiveRouteEpoch}; transport_epoch={state.TransportEpoch}; bridge_recovery_generation={state.BridgeRecoveryGeneration}; checkpoint_request_id={SanitizeLogToken(state.CheckpointRequestId ?? "none")}; authority_reason={state.AuthorityReason}; reason={SanitizeLogToken(reason)}");
    }

    private FileTransferRegularV4RecoveryLivenessState MarkFileTransferRegularV4RecoveryLivenessStarted(
        FileTransferReceiveRecoveryRequest request,
        string? sessionId,
        string transferId,
        string reason)
    {
        var normalizedSessionId = SanitizeLogToken(sessionId ?? "none");
        var normalizedTransferId = SanitizeLogToken(transferId);
        var normalizedRoute = SanitizeLogToken(request.RouteToken ?? FileTransferRouteResolver.RegularNknV4FastToken);
        var normalizedAuthorityReason = SanitizeLogToken(request.AuthorityReason ?? reason);
        FileTransferRegularV4RecoveryLivenessState state;
        bool started = false;
        lock (fileTransferFallbackProofGate)
        {
            var existing = fileTransferRegularV4RecoveryLivenessState;
            if (existing is not null &&
                !existing.Completed &&
                string.Equals(existing.SessionId, normalizedSessionId, StringComparison.Ordinal) &&
                string.Equals(existing.TransferId, normalizedTransferId, StringComparison.Ordinal) &&
                !existing.ReceiveProofObserved &&
                !existing.RecoveryExhausted)
            {
                state = existing;
            }
            else
            {
                var generation = existing is null ? 1 : existing.Generation + 1;
                state = new FileTransferRegularV4RecoveryLivenessState
                {
                    SessionId = normalizedSessionId,
                    TransferId = normalizedTransferId,
                    Generation = generation,
                    RouteToken = normalizedRoute,
                    ProtocolVersion = request.ProtocolVersion,
                    LiveRouteEpoch = Math.Max(0, request.LiveRouteEpoch),
                    AuthorityReason = normalizedAuthorityReason,
                    CreatedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    LivenessDeferralDeadlineUtcMs = DateTimeOffset.UtcNow
                        .Add(FileTransferRegularV4RecoveryLivenessDeferral)
                        .ToUnixTimeMilliseconds(),
                };
                fileTransferRegularV4RecoveryLivenessState = state;
                started = true;
            }
        }

        if (started)
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=filetransfer_regular_v4_recovery_liveness_started; session_id={state.SessionId}; transfer_id={state.TransferId}; generation={state.Generation}; route={state.RouteToken}; protocol_version={state.ProtocolVersion}; live_route_epoch={state.LiveRouteEpoch}; authority_reason={state.AuthorityReason}; reason={reason}");
        }

        return state;
    }

    private void MarkFileTransferRegularV4RecoveryLivenessBridgeRecoveryRequested(
        FileTransferRegularV4RecoveryLivenessState state,
        string reason)
    {
        var shouldLog = false;
        lock (fileTransferFallbackProofGate)
        {
            if (ReferenceEquals(fileTransferRegularV4RecoveryLivenessState, state) &&
                !state.BridgeRecoveryRequested)
            {
                state.BridgeRecoveryRequested = true;
                RefreshFileTransferRegularV4RecoveryLivenessDeadlineUnsafe(state);
                shouldLog = true;
            }
        }

        if (!shouldLog)
        {
            return;
        }

        LocalOperationalLog.Warn(
            "NKN.Tuna",
            $"event=filetransfer_regular_v4_recovery_liveness_bridge_recovery_requested; session_id={state.SessionId}; transfer_id={state.TransferId}; generation={state.Generation}; route={state.RouteToken}; protocol_version={state.ProtocolVersion}; live_route_epoch={state.LiveRouteEpoch}; authority_reason={state.AuthorityReason}; reason={SanitizeLogToken(reason)}");
    }

    private void MarkFileTransferRegularV4RecoveryLivenessBridgeRecoveryLifecycle(
        string lifecycle,
        string? reason)
    {
        FileTransferRegularV4RecoveryLivenessState? state;
        bool shouldLog = false;
        lock (fileTransferFallbackProofGate)
        {
            state = fileTransferRegularV4RecoveryLivenessState;
            if (state is null ||
                state.Completed)
            {
                return;
            }

            switch (lifecycle)
            {
                case "started":
                    RefreshFileTransferRegularV4RecoveryLivenessDeadlineUnsafe(state);
                    state.BridgeRecoveryStartedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (!state.BridgeRecoveryStarted)
                    {
                        state.BridgeRecoveryStarted = true;
                        shouldLog = true;
                    }

                    break;
                case "completed":
                    RefreshFileTransferRegularV4RecoveryLivenessDeadlineUnsafe(state);
                    if (!state.BridgeRecoveryCompleted)
                    {
                        state.BridgeRecoveryCompleted = true;
                        shouldLog = true;
                    }

                    break;
                case "receive_resumed":
                    shouldLog = true;

                    break;
                case "exhausted":
                    if (!state.RecoveryExhausted)
                    {
                        state.RecoveryExhausted = true;
                        shouldLog = true;
                    }

                    break;
            }
        }

        if (!shouldLog || state is null)
        {
            return;
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=filetransfer_regular_v4_recovery_liveness_bridge_lifecycle; lifecycle={SanitizeLogToken(lifecycle)}; session_id={state.SessionId}; transfer_id={state.TransferId}; generation={state.Generation}; route={state.RouteToken}; protocol_version={state.ProtocolVersion}; live_route_epoch={state.LiveRouteEpoch}; authority_reason={state.AuthorityReason}; liveness_deferral_deadline_utc_ms={state.LivenessDeferralDeadlineUtcMs}; reason={SanitizeLogToken(reason ?? "none")}");
    }

    private void MarkFileTransferRegularV4RecoveryLivenessReceiveProofReceived(
        string? sessionId,
        string? transferId,
        string proofKind,
        string lane)
    {
        var normalizedSessionId = SanitizeLogToken(sessionId ?? "none");
        var normalizedTransferId = SanitizeLogToken(transferId ?? "none");
        FileTransferRegularV4RecoveryLivenessState? state;
        var shouldLog = false;
        lock (fileTransferFallbackProofGate)
        {
            state = fileTransferRegularV4RecoveryLivenessState;
            if (state is not null &&
                !state.Completed &&
                !state.ReceiveProofObserved &&
                string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal) &&
                string.Equals(state.TransferId, normalizedTransferId, StringComparison.Ordinal))
            {
                state.ReceiveProofObserved = true;
                shouldLog = true;
            }
        }

        if (!shouldLog || state is null)
        {
            return;
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=filetransfer_regular_v4_recovery_liveness_receive_proof_observed; session_id={state.SessionId}; transfer_id={state.TransferId}; generation={state.Generation}; route={state.RouteToken}; protocol_version={state.ProtocolVersion}; live_route_epoch={state.LiveRouteEpoch}; authority_reason={state.AuthorityReason}; proof={SanitizeLogToken(proofKind)}; lane={SanitizeLogToken(lane)}");
        ScheduleRuntimeUnlockRetryAfterRecoveryIfArmed("regular_v4_receive_proof_observed");
    }

    private bool TryGetActiveRegularV4RecoveryLivenessStatus(
        string sessionId,
        out bool receiveProofObserved,
        out bool terminal,
        out bool deadlineExpired,
        out string stateReason,
        out long deadlineRemainingMs)
    {
        receiveProofObserved = false;
        terminal = false;
        deadlineExpired = false;
        stateReason = "none";
        deadlineRemainingMs = 0;

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var normalizedSessionId = SanitizeLogToken(sessionId);
        lock (fileTransferFallbackProofGate)
        {
            var state = fileTransferRegularV4RecoveryLivenessState;
            if (state is null ||
                !string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal))
            {
                return false;
            }

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            receiveProofObserved = state.ReceiveProofObserved;
            terminal = state.Completed || state.RecoveryExhausted;
            deadlineRemainingMs = Math.Max(0, state.LivenessDeferralDeadlineUtcMs - nowMs);
            deadlineExpired = state.LivenessDeferralDeadlineUtcMs > 0 &&
                              nowMs >= state.LivenessDeferralDeadlineUtcMs;
            stateReason = state.ReceiveProofObserved
                ? "regular_v4_receive_proof"
                : state.Completed
                    ? "regular_v4_recovery_completed"
                    : state.RecoveryExhausted
                        ? "regular_v4_recovery_exhausted"
                        : state.BridgeRecoveryCompleted
                            ? "regular_v4_bridge_recovery_completed_awaiting_filetransfer_proof"
                            : state.BridgeRecoveryStarted
                                ? "regular_v4_bridge_recovery_started_awaiting_filetransfer_proof"
                                : state.BridgeRecoveryRequested
                                    ? "regular_v4_bridge_recovery_requested_awaiting_filetransfer_proof"
                                    : "regular_v4_recovery_awaiting_filetransfer_proof";
            return true;
        }
    }

    private static void RefreshFileTransferRegularV4RecoveryLivenessDeadlineUnsafe(
        FileTransferRegularV4RecoveryLivenessState state)
    {
        var createdUtc = DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, state.CreatedUtcMs));
        var maxDeadline = createdUtc.Add(FileTransferRegularV4RecoveryLivenessMaxDeferral);
        var rollingDeadline = DateTimeOffset.UtcNow.Add(FileTransferRegularV4RecoveryLivenessDeferral);
        var nextDeadline = rollingDeadline < maxDeadline ? rollingDeadline : maxDeadline;
        var nextDeadlineMs = nextDeadline.ToUnixTimeMilliseconds();
        if (state.LivenessDeferralDeadlineUtcMs < nextDeadlineMs)
        {
            state.LivenessDeferralDeadlineUtcMs = nextDeadlineMs;
        }
    }

    private static bool IsRegularV4BridgeRecoveryStartedExpiredUnsafe(
        FileTransferRegularV4RecoveryLivenessState state,
        long nowUtcMs)
    {
        if (!state.BridgeRecoveryStarted ||
            state.BridgeRecoveryCompleted ||
            state.ReceiveProofObserved ||
            state.RecoveryExhausted ||
            state.Completed ||
            state.BridgeRecoveryStartedUtcMs <= 0)
        {
            return false;
        }

        return nowUtcMs - state.BridgeRecoveryStartedUtcMs >=
               (long)FileTransferRegularV4BridgeRecoveryStartedTimeout.TotalMilliseconds;
    }

    private static bool IsRegularV4BridgeRecoveryStartedRuntimeUnlockProbeReadyUnsafe(
        FileTransferRegularV4RecoveryLivenessState state,
        long nowUtcMs)
    {
        if (!state.BridgeRecoveryStarted ||
            state.BridgeRecoveryCompleted ||
            state.ReceiveProofObserved ||
            state.RecoveryExhausted ||
            state.Completed ||
            state.BridgeRecoveryStartedUtcMs <= 0)
        {
            return false;
        }

        var probeDelay = RuntimeUnlockRegularV4BridgeRecoveryStartedAuthorityProbeDelayOverrideForTests ??
                         RuntimeUnlockRegularV4BridgeRecoveryStartedAuthorityProbeDelay;
        return nowUtcMs - state.BridgeRecoveryStartedUtcMs >=
               (long)probeDelay.TotalMilliseconds;
    }

    private void MarkFileTransferFallbackLegAuthorityBridgeRecoveryLifecycle(
        string lifecycle,
        string? reason)
    {
        FileTransferFallbackLegAuthorityState? state;
        bool shouldLog = false;
        lock (fileTransferFallbackProofGate)
        {
            state = fileTransferFallbackLegAuthorityState;
            if (state is null ||
                state.Completed)
            {
                return;
            }

            switch (lifecycle)
            {
                case "started":
                    if (!state.BridgeRecoveryStarted)
                    {
                        state.BridgeRecoveryStarted = true;
                        RefreshFileTransferFallbackLegAuthorityLivenessDeadlineUnsafe(state);
                        shouldLog = true;
                    }

                    break;
                case "completed":
                    if (!state.BridgeRecoveryCompleted)
                    {
                        state.BridgeRecoveryCompleted = true;
                        RefreshFileTransferFallbackLegAuthorityLivenessDeadlineUnsafe(state);
                        shouldLog = true;
                    }

                    break;
                case "receive_resumed":
                    if (!state.ReceiveProofObserved)
                    {
                        state.ReceiveProofObserved = true;
                        shouldLog = true;
                    }

                    break;
                case "exhausted":
                    if (!state.RecoveryExhausted)
                    {
                        state.RecoveryExhausted = true;
                        shouldLog = true;
                    }

                    break;
            }
        }

        if (!shouldLog || state is null)
        {
            return;
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=filetransfer_fallback_leg_authority_bridge_lifecycle; lifecycle={SanitizeLogToken(lifecycle)}; session_id={state.SessionId}; transfer_id={state.TransferId}; leg_generation={state.LegGeneration}; route={state.RouteToken}; protocol_version={state.ProtocolVersion}; live_route_epoch={state.LiveRouteEpoch}; transport_epoch={state.TransportEpoch}; bridge_recovery_generation={state.BridgeRecoveryGeneration}; checkpoint_request_id={SanitizeLogToken(state.CheckpointRequestId ?? "none")}; authority_reason={state.AuthorityReason}; reason={SanitizeLogToken(reason ?? "none")}");
    }

    private static void RefreshFileTransferFallbackLegAuthorityLivenessDeadlineUnsafe(
        FileTransferFallbackLegAuthorityState state)
    {
        var createdUtc = DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, state.CreatedUtcMs));
        var maxDeadline = createdUtc.Add(FileTransferFallbackRecoveryLivenessMaxDeferral);
        var rollingDeadline = DateTimeOffset.UtcNow.Add(FileTransferFallbackRecoveryLivenessDeferral);
        var nextDeadline = rollingDeadline <= maxDeadline ? rollingDeadline : maxDeadline;
        var nextDeadlineUtcMs = nextDeadline.ToUnixTimeMilliseconds();
        if (nextDeadlineUtcMs > state.LivenessDeferralDeadlineUtcMs)
        {
            state.LivenessDeferralDeadlineUtcMs = nextDeadlineUtcMs;
        }
    }

    public bool TryGetActiveFileTransferRecoveryLivenessSnapshot(
        string sessionId,
        out FileTransferRecoveryLivenessSnapshot snapshot)
    {
        snapshot = default!;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        FileTransferFallbackLegAuthorityState? state;
        lock (fileTransferFallbackProofGate)
        {
            state = fileTransferFallbackLegAuthorityState;
            if (state is not null &&
                string.Equals(state.SessionId, sessionId.Trim(), StringComparison.Ordinal) &&
                !state.Completed)
            {
                var createdUtc = DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, state.CreatedUtcMs));
                var recoveryState =
                    state.RecoveryExhausted ? FileTransferRecoveryLivenessState.Exhausted :
                    state.ReceiveProofObserved ? FileTransferRecoveryLivenessState.ReceiveProofObserved :
                    state.BridgeRecoveryCompleted ? FileTransferRecoveryLivenessState.BridgeRecoveryCompletedAwaitingProof :
                    state.BridgeRecoveryStarted ? FileTransferRecoveryLivenessState.BridgeRecoveryStarted :
                    state.BridgeRecoveryRequested ? FileTransferRecoveryLivenessState.BridgeRecoveryRequested :
                    FileTransferRecoveryLivenessState.AuthorityActive;
                var terminalRecommended =
                    recoveryState is FileTransferRecoveryLivenessState.Exhausted or
                        FileTransferRecoveryLivenessState.ReceiveProofObserved or
                        FileTransferRecoveryLivenessState.Completed;

                snapshot = new FileTransferRecoveryLivenessSnapshot(
                    state.SessionId,
                    state.TransferId,
                    state.RouteToken,
                    state.ProtocolVersion,
                    state.LiveRouteEpoch,
                    state.LegGeneration,
                    state.BridgeRecoveryGeneration,
                    state.TransportEpoch,
                    state.CheckpointRequestId,
                    state.AuthorityReason,
                    recoveryState,
                    createdUtc,
                    DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, state.LivenessDeferralDeadlineUtcMs)),
                    state.BridgeRecoveryRequested,
                    state.BridgeRecoveryStarted,
                    state.BridgeRecoveryCompleted,
                    state.ReceiveProofObserved,
                    state.RecoveryExhausted,
                    state.Completed,
                    terminalRecommended);
                return true;
            }

            var regularV4State = fileTransferRegularV4RecoveryLivenessState;
            if (regularV4State is not null &&
                string.Equals(regularV4State.SessionId, SanitizeLogToken(sessionId), StringComparison.Ordinal) &&
                !regularV4State.Completed)
            {
                var regularCreatedUtc = DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, regularV4State.CreatedUtcMs));
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var regularBridgeRecoveryStartedExpired =
                    IsRegularV4BridgeRecoveryStartedExpiredUnsafe(regularV4State, nowMs);
                var regularRecoveryState =
                    (regularV4State.RecoveryExhausted || regularBridgeRecoveryStartedExpired) ? FileTransferRecoveryLivenessState.Exhausted :
                    regularV4State.ReceiveProofObserved ? FileTransferRecoveryLivenessState.ReceiveProofObserved :
                    regularV4State.BridgeRecoveryCompleted ? FileTransferRecoveryLivenessState.BridgeRecoveryCompletedAwaitingProof :
                    regularV4State.BridgeRecoveryStarted ? FileTransferRecoveryLivenessState.BridgeRecoveryStarted :
                    regularV4State.BridgeRecoveryRequested ? FileTransferRecoveryLivenessState.BridgeRecoveryRequested :
                    FileTransferRecoveryLivenessState.AuthorityActive;
                var regularTerminalRecommended =
                    regularRecoveryState is FileTransferRecoveryLivenessState.Exhausted or
                        FileTransferRecoveryLivenessState.ReceiveProofObserved or
                        FileTransferRecoveryLivenessState.Completed;

                snapshot = new FileTransferRecoveryLivenessSnapshot(
                    regularV4State.SessionId,
                    regularV4State.TransferId,
                    regularV4State.RouteToken,
                    regularV4State.ProtocolVersion,
                    regularV4State.LiveRouteEpoch,
                    regularV4State.Generation,
                    BridgeRecoveryGeneration: regularV4State.Generation,
                    TransportEpoch: 0,
                    CheckpointRequestId: null,
                    regularV4State.AuthorityReason,
                    regularRecoveryState,
                    regularCreatedUtc,
                    DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, regularV4State.LivenessDeferralDeadlineUtcMs)),
                    regularV4State.BridgeRecoveryRequested,
                    regularV4State.BridgeRecoveryStarted,
                    regularV4State.BridgeRecoveryCompleted,
                    regularV4State.ReceiveProofObserved,
                    regularV4State.RecoveryExhausted || regularBridgeRecoveryStartedExpired,
                    regularV4State.Completed,
                    regularTerminalRecommended);
                return true;
            }

            return false;
        }
    }

    private void MarkFileTransferFallbackLegAuthorityCompleted(
        string? sessionId,
        string? transferId,
        string? routeToken,
        int protocolVersion,
        int liveRouteEpoch,
        int legGeneration,
        int bridgeRecoveryGeneration,
        long transportEpoch,
        string? checkpointRequestId,
        string? authorityReason,
        string proofKind,
        string? proofDirection = null)
    {
        var normalizedSessionId = SanitizeLogToken(sessionId ?? "none");
        var normalizedTransferId = SanitizeLogToken(transferId ?? "none");
        var normalizedRoute = SanitizeLogToken(routeToken ?? "post_tuna_fallback_v6");
        var normalizedAuthorityReason = SanitizeLogToken(authorityReason ?? "none");
        var direction = SanitizeLogToken(proofDirection ?? "(none)");
        FileTransferRouteHint? activeRouteHint = null;
        lock (gate)
        {
            if (fileTransferRouteHints.TryGetValue(normalizedTransferId, out var routeHint))
            {
                activeRouteHint = routeHint;
            }
        }

        if (activeRouteHint is { } routeHintSnapshot &&
            (routeHintSnapshot.Route != FileTransferRoute.PostTunaFallbackV6 ||
             routeHintSnapshot.ProtocolVersion != FileTransferProtocol.ProtocolVersionV6))
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=filetransfer_fallback_leg_authority_superseded_by_route_hint; direction={direction}; session_id={normalizedSessionId}; transfer_id={normalizedTransferId}; leg_generation={legGeneration}; route={normalizedRoute}; protocol_version={protocolVersion}; live_route_epoch={liveRouteEpoch}; transport_epoch={transportEpoch}; bridge_recovery_generation={bridgeRecoveryGeneration}; checkpoint_request_id={SanitizeLogToken(checkpointRequestId ?? "none")}; authority_reason={normalizedAuthorityReason}; superseded_by_route={SanitizeLogToken(routeHintSnapshot.Token)}; superseded_by_protocol_version={routeHintSnapshot.ProtocolVersion}; source={SanitizeLogToken(routeHintSnapshot.Source)}");
            return;
        }

        var shouldLog = true;
        lock (fileTransferFallbackProofGate)
        {
            if (fileTransferFallbackLegAuthorityState is { } state &&
                !state.Completed &&
                string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal) &&
                string.Equals(state.TransferId, normalizedTransferId, StringComparison.Ordinal) &&
                state.LegGeneration == legGeneration)
            {
                direction = state.Direction;
                state.ReceiveProofObserved = true;
                state.Completed = true;
            }
            else if (fileTransferFallbackLegAuthorityState is { Completed: true } completedState &&
                     string.Equals(completedState.SessionId, normalizedSessionId, StringComparison.Ordinal) &&
                     string.Equals(completedState.TransferId, normalizedTransferId, StringComparison.Ordinal) &&
                     completedState.LegGeneration == legGeneration)
            {
                direction = completedState.Direction;
                shouldLog = false;
            }
        }

        if (!shouldLog)
        {
            return;
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=filetransfer_fallback_leg_authority_completed; direction={direction}; session_id={normalizedSessionId}; transfer_id={normalizedTransferId}; leg_generation={legGeneration}; route={normalizedRoute}; protocol_version={protocolVersion}; live_route_epoch={liveRouteEpoch}; transport_epoch={transportEpoch}; bridge_recovery_generation={bridgeRecoveryGeneration}; checkpoint_request_id={SanitizeLogToken(checkpointRequestId ?? "none")}; authority_reason={normalizedAuthorityReason}; proof={SanitizeLogToken(proofKind)}");
    }

    private void ClearFileTransferFallbackProofAuthorityUnsafe()
    {
        fileTransferFallbackProofTransferId = null;
        fileTransferFallbackProofRouteToken = null;
        fileTransferFallbackProofProtocolVersion = 0;
        fileTransferFallbackProofLiveRouteEpoch = 0;
        fileTransferFallbackProofLegGeneration = 0;
        fileTransferFallbackProofBridgeRecoveryGeneration = 0;
        fileTransferFallbackProofTransportEpoch = 0;
        fileTransferFallbackProofCheckpointRequestId = null;
        fileTransferFallbackProofAuthorityReason = null;
    }

    private static bool IsSessionLivenessReceiveRecoveryReason(string? reason)
        => string.Equals(
            reason?.Trim(),
            "session_liveness_timeout_pending",
            StringComparison.OrdinalIgnoreCase);

    public void ObserveRegularV4ControlFeedbackPressure(FileTransferRegularV4ControlFeedbackPressure pressure)
    {
        var sessionId = string.IsNullOrWhiteSpace(pressure.SessionId)
            ? currentSessionSecurityState.SessionId?.Value
            : pressure.SessionId.Trim();
        var transferId = string.IsNullOrWhiteSpace(pressure.TransferId) ? "(none)" : pressure.TransferId.Trim();
        var reason = string.IsNullOrWhiteSpace(pressure.Reason)
            ? "regular_v4_control_feedback_pressure"
            : pressure.Reason.Trim();

        if (client is not RealNknClientAdapter realClient)
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=filetransfer_regular_v4_control_feedback_pressure_unsupported; session_id={SanitizeLogToken(sessionId ?? "none")}; transfer_id={SanitizeLogToken(transferId)}; reason={SanitizeLogToken(reason)}");
            return;
        }

        realClient.ReportRegularV4ControlFeedbackPressure(
            transferId,
            reason,
            pressure.CreditExhaustedTimeMs,
            pressure.FrontierLagChunks,
            pressure.PendingRepairCount);
    }

    public void ObserveFileTransferRouteCompleted(FileTransferRouteCompletedNotification notification)
    {
        var normalizedTransferId = string.IsNullOrWhiteSpace(notification.TransferId)
            ? string.Empty
            : notification.TransferId.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedTransferId))
        {
            var clearedTransportState = false;
            lock (controlSecureStateGate)
            {
                if (fileTransferStates.TryGetValue(normalizedTransferId, out var currentState) &&
                    !currentState.IsTerminal)
                {
                    CommitFileTransferStateLocked(
                        normalizedTransferId,
                        currentState with { Phase = FileTransferTransportPhase.Completed });
                    clearedTransportState = true;
                }
            }

            if (clearedTransportState)
            {
                Log(
                    $"event=filetransfer_transport_state_completed_by_service_terminal; transfer_id={SanitizeLogToken(normalizedTransferId)}; session_id={SanitizeLogToken(notification.SessionId)}; route={SanitizeLogToken(notification.RouteToken)}; protocol_version={notification.ProtocolVersion}");
            }
        }

        if (!string.Equals(notification.RouteToken, FileTransferRouteResolver.PostTunaFallbackV6Token, StringComparison.Ordinal) ||
            notification.ProtocolVersion != FileTransferProtocol.ProtocolVersionV6)
        {
            MarkFileTransferFallbackLegAuthoritySupersededByRouteHint(
                notification.TransferId,
                notification.RouteToken ?? "(none)",
                notification.ProtocolVersion,
                "service_terminal");

            if (client is RealNknClientAdapter realClient)
            {
                realClient.ClearActiveFileTransferPostTunaFallbackRuntime(
                    notification.TransferId,
                    "service_terminal");
            }

            if (string.Equals(notification.RouteToken, FileTransferRouteResolver.RegularNknV4FastToken, StringComparison.Ordinal) &&
                notification.ProtocolVersion == FileTransferProtocol.ProtocolVersionV4)
            {
                MarkFileTransferRegularV4RecoveryLivenessCompleted(
                    notification.SessionId,
                    notification.TransferId,
                    "regular_nkn_v4_fast_transfer_completed");
            }

            return;
        }

        ConsumePostTunaFileFallbackRoute(
            notification.SessionId,
            notification.TransferId,
            "post_tuna_fallback_v6_transfer_completed");
    }

    private void MarkFileTransferRegularV4RecoveryLivenessCompleted(
        string? sessionId,
        string? transferId,
        string reason)
    {
        var normalizedSessionId = SanitizeLogToken(sessionId ?? "none");
        var normalizedTransferId = SanitizeLogToken(transferId ?? "none");
        FileTransferRegularV4RecoveryLivenessState? state;
        var shouldLog = false;
        lock (fileTransferFallbackProofGate)
        {
            state = fileTransferRegularV4RecoveryLivenessState;
            if (state is not null &&
                !state.Completed &&
                string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal) &&
                string.Equals(state.TransferId, normalizedTransferId, StringComparison.Ordinal))
            {
                state.Completed = true;
                shouldLog = true;
            }
        }

        if (!shouldLog || state is null)
        {
            return;
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=filetransfer_regular_v4_recovery_liveness_completed; session_id={state.SessionId}; transfer_id={state.TransferId}; generation={state.Generation}; route={state.RouteToken}; protocol_version={state.ProtocolVersion}; live_route_epoch={state.LiveRouteEpoch}; authority_reason={state.AuthorityReason}; reason={SanitizeLogToken(reason)}");
    }

    private void MarkFileTransferRegularV4RecoveryLivenessSupersededByRouteHint(
        string? transferId,
        string routeToken,
        int protocolVersion,
        string source)
    {
        var normalizedTransferId = SanitizeLogToken(transferId ?? "none");
        var normalizedRoute = SanitizeLogToken(routeToken);
        FileTransferRegularV4RecoveryLivenessState? state;
        var shouldLog = false;
        lock (fileTransferFallbackProofGate)
        {
            state = fileTransferRegularV4RecoveryLivenessState;
            if (state is not null &&
                !state.Completed &&
                string.Equals(state.TransferId, normalizedTransferId, StringComparison.Ordinal) &&
                !string.Equals(normalizedRoute, FileTransferRouteResolver.RegularNknV4FastToken, StringComparison.Ordinal))
            {
                state.Completed = true;
                shouldLog = true;
            }
        }

        if (!shouldLog || state is null)
        {
            return;
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=filetransfer_regular_v4_recovery_liveness_completed; session_id={state.SessionId}; transfer_id={state.TransferId}; generation={state.Generation}; route={state.RouteToken}; protocol_version={state.ProtocolVersion}; live_route_epoch={state.LiveRouteEpoch}; authority_reason={state.AuthorityReason}; reason=regular_v4_recovery_superseded_by_route_hint; superseded_by_route={normalizedRoute}; superseded_by_protocol_version={protocolVersion}; source={SanitizeLogToken(source)}");
    }

    private bool ShouldIgnoreStalePostTunaFallbackLegAuthorityRequest(
        FileTransferReceiveRecoveryRequest request,
        string? sessionId,
        string transferId,
        string reason)
    {
        if (!HasFileTransferFallbackLegAuthority(request) ||
            string.IsNullOrWhiteSpace(transferId))
        {
            return false;
        }

        FileTransferRouteHint routeHint;
        lock (gate)
        {
            if (!fileTransferRouteHints.TryGetValue(transferId.Trim(), out routeHint))
            {
                return false;
            }
        }

        if (routeHint.Route == FileTransferRoute.PostTunaFallbackV6 &&
            routeHint.ProtocolVersion == FileTransferProtocol.ProtocolVersionV6)
        {
            return false;
        }

        MarkFileTransferFallbackLegAuthoritySupersededByRouteHint(
            transferId,
            routeHint.Token,
            routeHint.ProtocolVersion,
            "stale_fallback_authority_request");

        if (client is RealNknClientAdapter realClient)
        {
            realClient.ClearActiveFileTransferPostTunaFallbackRuntime(
                transferId,
                "stale_fallback_authority_request");
        }

        LocalOperationalLog.Warn(
            "NKN.Tuna",
            "event=filetransfer_fallback_leg_authority_stale_request_ignored; " +
            $"session_id={SanitizeLogToken(sessionId ?? "none")}; transfer_id={SanitizeLogToken(transferId)}; reason={SanitizeLogToken(reason)}; " +
            $"requested_route={SanitizeLogToken(request.RouteToken ?? "none")}; requested_protocol_version={request.ProtocolVersion}; requested_live_route_epoch={request.LiveRouteEpoch}; requested_leg_generation={request.TransferLegGeneration}; requested_transport_epoch={request.TransportEpoch}; " +
            $"current_route={SanitizeLogToken(routeHint.Token)}; current_protocol_version={routeHint.ProtocolVersion}; current_route_source={SanitizeLogToken(routeHint.Source)}");
        return true;
    }

    private void MarkFileTransferFallbackLegAuthoritySupersededByRouteHint(
        string? transferId,
        string routeToken,
        int protocolVersion,
        string source)
    {
        if (string.IsNullOrWhiteSpace(transferId))
        {
            return;
        }

        var normalizedTransferId = SanitizeLogToken(transferId.Trim());
        var normalizedRoute = SanitizeLogToken(routeToken);
        FileTransferFallbackLegAuthorityState? state;
        var shouldLog = false;
        lock (fileTransferFallbackProofGate)
        {
            state = fileTransferFallbackLegAuthorityState;
            if (state is not null &&
                !state.Completed &&
                string.Equals(state.TransferId, normalizedTransferId, StringComparison.Ordinal) &&
                (!string.Equals(normalizedRoute, FileTransferRouteResolver.PostTunaFallbackV6Token, StringComparison.Ordinal) ||
                 protocolVersion != FileTransferProtocol.ProtocolVersionV6))
            {
                state.Completed = true;
                ClearFileTransferFallbackProofAuthorityUnsafe();
                shouldLog = true;
            }
        }

        if (!shouldLog || state is null)
        {
            return;
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=filetransfer_fallback_leg_authority_superseded_by_route_hint; direction={state.Direction}; session_id={state.SessionId}; transfer_id={state.TransferId}; leg_generation={state.LegGeneration}; route={state.RouteToken}; protocol_version={state.ProtocolVersion}; live_route_epoch={state.LiveRouteEpoch}; transport_epoch={state.TransportEpoch}; bridge_recovery_generation={state.BridgeRecoveryGeneration}; checkpoint_request_id={SanitizeLogToken(state.CheckpointRequestId ?? "none")}; authority_reason={state.AuthorityReason}; superseded_by_route={normalizedRoute}; superseded_by_protocol_version={protocolVersion}; source={SanitizeLogToken(source)}");
    }

    internal bool IsAccelerationUserStoppedForCurrentSessionForTests => IsAccelerationUserStoppedForCurrentSession();

    internal void SetAccelerationAcceptedForTests(NknAccelerationLaneKind lanes, string? sessionId = null)
    {
        string? acceptedSessionId;
        lock (accelerationGate)
        {
            accelerationSessionId = string.IsNullOrWhiteSpace(sessionId)
                ? currentSessionSecurityState.SessionId?.Value
                : sessionId.Trim();
            accelerationNegotiatedLanes = lanes;
            acceptedSessionId = accelerationSessionId;
        }

        RequestFileTransferTunaActivationHandoff(acceptedSessionId, lanes, "test_accept");
        NotifyTransportAccelerationStateChanged("test_accept");
    }

    private bool StartTunaFallbackProofIfNeeded(string reason, string? sessionId, NknAccelerationLaneKind lanes)
    {
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
        if (string.IsNullOrWhiteSpace(normalizedSessionId) || lanes == NknAccelerationLaneKind.None)
        {
            return false;
        }

        var normalizedReason = SanitizeLogToken(reason);
        if ((lanes & NknAccelerationLaneKind.File) == NknAccelerationLaneKind.File &&
            !HasActiveFileTransferDataSessionsForRecovery())
        {
            lanes &= ~NknAccelerationLaneKind.File;
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=filetransfer_post_tuna_fallback_v6_route_suppressed_no_active_transfer; session_id={SanitizeLogToken(normalizedSessionId)}; reason={normalizedReason}; remaining_lanes={FormatAccelerationLanesForLog(lanes)}");
            if (lanes == NknAccelerationLaneKind.None)
            {
                return false;
            }
        }

        TunaFallbackProofState? stateToLog = null;
        lock (accelerationGate)
        {
            if (tunaFallbackProofState is { } existing &&
                string.Equals(existing.SessionId, normalizedSessionId, StringComparison.Ordinal))
            {
                if (IsFileTransferFallbackFinalForLanes(existing, lanes))
                {
                    if (ShouldReplaceFinalFileTransferFallbackForFreshUserStop(existing, lanes, normalizedReason))
                    {
                        LocalOperationalLog.Info(
                            "NKN.Tuna",
                            $"event=filetransfer_v6_fallback_start_replaced_final_for_user_stop; session_id={SanitizeLogToken(normalizedSessionId)}; reason={normalizedReason}; existing_reason={existing.Reason}; existing_fallback_epoch={existing.Epoch}; file_state={FormatTunaFallbackLaneState(existing.FileState)}; file_v6_epoch_state={FormatTunaFallbackFileV6EpochState(existing)}");
                    }
                    else
                    {
                        LocalOperationalLog.Info(
                            "NKN.Tuna",
                            $"event=filetransfer_v6_fallback_start_suppressed_final; session_id={SanitizeLogToken(normalizedSessionId)}; reason={normalizedReason}; existing_reason={existing.Reason}; file_state={FormatTunaFallbackLaneState(existing.FileState)}; file_v6_epoch_state={FormatTunaFallbackFileV6EpochState(existing)}");
                        return false;
                    }
                }
                else if (!existing.AccelerationUsedAfterFallback)
                {
                    return false;
                }
            }

            var epoch = Interlocked.Increment(ref tunaFallbackProofNextEpoch);
            tunaFallbackProofState = new TunaFallbackProofState
            {
                Epoch = epoch,
                SessionId = normalizedSessionId,
                Reason = normalizedReason,
                StartedUtc = DateTimeOffset.UtcNow,
                Lanes = lanes,
            };
            stateToLog = tunaFallbackProofState;
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_fallback_started; session_id={stateToLog.SessionId}; fallback_epoch={stateToLog.Epoch}; reason={stateToLog.Reason}; lanes={FormatAccelerationLanesForLog(stateToLog.Lanes)}");
        if (IsMixedFallbackLaneSet(stateToLog.Lanes))
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_mixed_handoff_started; session_id={stateToLog.SessionId}; fallback_epoch={stateToLog.Epoch}; reason={stateToLog.Reason}; lanes={FormatAccelerationLanesForLog(stateToLog.Lanes)}");
        }

        return true;
    }

    private void StartTunaFallbackProofAndRebindIfNeeded(string reason, string? sessionId, NknAccelerationLaneKind lanes)
    {
        if (!StartTunaFallbackProofIfNeeded(reason, sessionId, lanes))
        {
            return;
        }

        RebindFileTransferDataSessionsForTunaFallback(reason, sessionId, lanes);
        RebindScreenShareDataSessionsForTunaFallback(reason, sessionId, lanes);
    }

    public void ObserveFileTransferV6TransportEpoch(FileTransferV6TransportEpochSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.SessionId) ||
            string.IsNullOrWhiteSpace(snapshot.TransferId) ||
            snapshot.TransportEpoch <= 0)
        {
            return;
        }

        if (ShouldIgnoreStaleRecoveredFileTransferFallbackEpochSnapshot(snapshot))
        {
            return;
        }

        var key = new FileTransferV6TransportEpochKey(
            snapshot.SessionId,
            snapshot.TransferId,
            snapshot.Direction,
            snapshot.TransportEpoch);
        var updated = false;
        lock (fileTransferV6TransportEpochGate)
        {
            if (snapshot.State == V6TransportEpochState.TargetProofPending)
            {
                observedFileTransferV6TransportEpochStartedCount++;
                if (snapshot.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                    snapshot.TargetTransport == FileTransferTransportKind.Tuna)
                {
                    observedFileTransferV6NormalToTunaActivationStartedCount++;
                }
            }
            else if (snapshot.State == V6TransportEpochState.Recovered)
            {
                observedFileTransferV6TransportEpochRecoveredCount++;
                if (snapshot.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                    snapshot.TargetTransport == FileTransferTransportKind.Tuna)
                {
                    observedFileTransferV6NormalToTunaActivationRecoveredCount++;
                }

                if (snapshot.TargetTransport == FileTransferTransportKind.RegularNkn &&
                    snapshot.HandoffKind is FileTransferTransportHandoffKind.TunaToNormalFallback or FileTransferTransportHandoffKind.RegularNknRecovery)
                {
                    lastRecoveredFileTransferV6RegularNknEpoch = snapshot;
                }
            }
            else if (snapshot.State == V6TransportEpochState.WaitingForTargetTransport)
            {
                observedFileTransferV6TransportEpochWaitingCount++;
            }
            else if (snapshot.State == V6TransportEpochState.Terminal)
            {
                observedFileTransferV6TransportEpochTerminalCount++;
            }

            if (snapshot.IsUnresolved)
            {
                unresolvedFileTransferV6TransportEpochs[key] = snapshot;
                updated = true;
            }
            else
            {
                updated = unresolvedFileTransferV6TransportEpochs.Remove(key);
            }
        }

        if (updated)
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=filetransfer_v6_epoch_observed; session_id={SanitizeLogToken(snapshot.SessionId)}; transfer_id={SanitizeLogToken(snapshot.TransferId)}; direction={snapshot.Direction.ToString().ToLowerInvariant()}; transport_epoch={snapshot.TransportEpoch}; state={FormatFileTransferV6TransportEpochStateForLog(snapshot.State)}; handoff_kind={FormatFileTransferTransportHandoffKindForLog(snapshot.HandoffKind)}; target_transport={FormatFileTransferTransportKindForLog(snapshot.TargetTransport)}; unresolved={(snapshot.IsUnresolved ? 1 : 0)}; reason={SanitizeLogToken(snapshot.Reason)}");
        }

        if (snapshot.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
            snapshot.TargetTransport == FileTransferTransportKind.Tuna)
        {
            ClearPendingFileTransferV6Handoffs("epoch_observed", snapshot.SessionId);
        }

        if (ShouldIgnoreFinalFileTransferFallbackEpochSnapshot(snapshot))
        {
            return;
        }

        ApplyFileTransferV6TransportEpochObservationToFallbackState(snapshot);
    }

    private void ApplyFileTransferV6TransportEpochObservationToFallbackState(FileTransferV6TransportEpochSnapshot snapshot)
    {
        if (snapshot.TargetTransport != FileTransferTransportKind.RegularNkn ||
            snapshot.HandoffKind is not FileTransferTransportHandoffKind.TunaToNormalFallback and not FileTransferTransportHandoffKind.RegularNknRecovery)
        {
            return;
        }

        MarkTunaFallbackFileV6EpochState(snapshot.SessionId, snapshot.TransportEpoch, snapshot.State, SanitizeLogToken(snapshot.Reason));

        if (snapshot.State == V6TransportEpochState.WaitingForTargetTransport)
        {
            MarkTunaFallbackLaneState(
                snapshot.SessionId,
                lane: NknAccelerationLaneKind.File,
                state: TunaFallbackLaneState.WaitingForRegularNkn,
                reason: SanitizeLogToken(snapshot.Reason));
            return;
        }

        if (snapshot.State == V6TransportEpochState.Recovered)
        {
            CompleteFileTransferFallbackNknProofFromV6Epoch(snapshot);
        }
    }

    private bool ShouldIgnoreStaleRecoveredFileTransferFallbackEpochSnapshot(FileTransferV6TransportEpochSnapshot snapshot)
    {
        if (!snapshot.IsUnresolved ||
            snapshot.TargetTransport != FileTransferTransportKind.RegularNkn ||
            snapshot.HandoffKind is not FileTransferTransportHandoffKind.TunaToNormalFallback and not FileTransferTransportHandoffKind.RegularNknRecovery)
        {
            return false;
        }

        lock (accelerationGate)
        {
            if (!TryGetCurrentTunaFallbackProofStateUnsafe(snapshot.SessionId, out var current) ||
                !ShouldIgnoreFinalFileTransferFallbackEpochSnapshotUnsafe(snapshot, current))
            {
                return false;
            }

            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=filetransfer_v6_epoch_observation_ignored_final_fallback; session_id={SanitizeLogToken(snapshot.SessionId)}; transfer_id={SanitizeLogToken(snapshot.TransferId)}; direction={snapshot.Direction.ToString().ToLowerInvariant()}; transport_epoch={snapshot.TransportEpoch}; state={FormatFileTransferV6TransportEpochStateForLog(snapshot.State)}; handoff_kind={FormatFileTransferTransportHandoffKindForLog(snapshot.HandoffKind)}; target_transport={FormatFileTransferTransportKindForLog(snapshot.TargetTransport)}; reason={SanitizeLogToken(snapshot.Reason)}; file_state={FormatTunaFallbackLaneState(current.FileState)}; file_v6_epoch_state={FormatTunaFallbackFileV6EpochState(current)}; file_v6_transport_epoch={current.FileV6TransportEpoch}");
            return true;
        }
    }

    private bool ShouldIgnoreFinalFileTransferFallbackEpochSnapshot(FileTransferV6TransportEpochSnapshot snapshot)
    {
        if (snapshot.TargetTransport != FileTransferTransportKind.RegularNkn ||
            snapshot.HandoffKind is not FileTransferTransportHandoffKind.TunaToNormalFallback and not FileTransferTransportHandoffKind.RegularNknRecovery)
        {
            return false;
        }

        lock (accelerationGate)
        {
            if (!TryGetCurrentTunaFallbackProofStateUnsafe(snapshot.SessionId, out var current) ||
                !ShouldIgnoreFinalFileTransferFallbackEpochSnapshotUnsafe(snapshot, current))
            {
                return false;
            }

            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=filetransfer_v6_epoch_observation_ignored_final_fallback; session_id={SanitizeLogToken(snapshot.SessionId)}; transfer_id={SanitizeLogToken(snapshot.TransferId)}; direction={snapshot.Direction.ToString().ToLowerInvariant()}; transport_epoch={snapshot.TransportEpoch}; state={FormatFileTransferV6TransportEpochStateForLog(snapshot.State)}; handoff_kind={FormatFileTransferTransportHandoffKindForLog(snapshot.HandoffKind)}; target_transport={FormatFileTransferTransportKindForLog(snapshot.TargetTransport)}; reason={SanitizeLogToken(snapshot.Reason)}; file_state={FormatTunaFallbackLaneState(current.FileState)}; file_v6_epoch_state={FormatTunaFallbackFileV6EpochState(current)}; file_v6_transport_epoch={current.FileV6TransportEpoch}");
            return true;
        }
    }

    private static bool ShouldIgnoreFinalFileTransferFallbackEpochSnapshotUnsafe(
        FileTransferV6TransportEpochSnapshot snapshot,
        TunaFallbackProofState current)
    {
        if (!IsFileTransferFallbackFinalForLanes(current, NknAccelerationLaneKind.File))
        {
            return false;
        }

        if (current.FileV6TransportEpoch > 0 &&
            snapshot.TransportEpoch > 0 &&
            snapshot.TransportEpoch < current.FileV6TransportEpoch)
        {
            return true;
        }

        // Once Core has entered an unresolved proof/waiting state for the same epoch,
        // it is still the recovery authority. Only suppress new proof-pending noise
        // caused by secondary sidecar errors after a completed fallback.
        return snapshot.State == V6TransportEpochState.TargetProofPending &&
               snapshot.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback;
    }

    private bool TryGetUnresolvedFileTransferV6TransportEpochForCurrentSession(out FileTransferV6TransportEpochSnapshot snapshot)
    {
        var sessionId = currentSessionSecurityState.SessionId?.Value;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            snapshot = default!;
            return false;
        }

        lock (fileTransferV6TransportEpochGate)
        {
            foreach (var candidate in unresolvedFileTransferV6TransportEpochs.Values)
            {
                if (string.Equals(candidate.SessionId, sessionId, StringComparison.Ordinal) &&
                    candidate.IsUnresolved)
                {
                    snapshot = candidate;
                    return true;
                }
            }
        }

        snapshot = default!;
        return false;
    }

    private bool ShouldSuppressFileTransferControlReceiveStallRecoveryBroadcast(
        string reason,
        out string suppressReason,
        out long cooldownRemainingMs)
    {
        suppressReason = "none";
        cooldownRemainingMs = 0;
        if (!reason.StartsWith("control_receive_stalled", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var sessionId = currentSessionSecurityState.SessionId?.Value;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        lock (fileTransferV6TransportEpochGate)
        {
            foreach (var candidate in unresolvedFileTransferV6TransportEpochs.Values)
            {
                if (string.Equals(candidate.SessionId, sessionId, StringComparison.Ordinal) &&
                    candidate.TargetTransport == FileTransferTransportKind.RegularNkn &&
                    candidate.HandoffKind is FileTransferTransportHandoffKind.TunaToNormalFallback or FileTransferTransportHandoffKind.RegularNknRecovery &&
                    candidate.IsUnresolved)
                {
                    suppressReason = "regular_nkn_epoch_unresolved";
                    return true;
                }
            }

            if (lastRecoveredFileTransferV6RegularNknEpoch is { } recovered &&
                string.Equals(recovered.SessionId, sessionId, StringComparison.Ordinal))
            {
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=filetransfer_control_receive_stall_recovery_after_recovered_epoch_allowed; session_id={SanitizeLogToken(sessionId)}; reason={SanitizeLogToken(reason)}; recovered_transport_epoch={recovered.TransportEpoch}; recovered_handoff_kind={FormatFileTransferTransportHandoffKindForLog(recovered.HandoffKind)}");
            }
        }

        var nowTick = Stopwatch.GetTimestamp();
        var lastTick = Volatile.Read(ref fileTransferControlReceiveStallRecoveryBroadcastLastTick);
        if (lastTick > 0)
        {
            var elapsed = Stopwatch.GetElapsedTime(lastTick, nowTick);
            if (elapsed < FileTransferControlReceiveStallRecoveryBroadcastCooldown)
            {
                suppressReason = "cooldown";
                cooldownRemainingMs = Math.Max(
                    0,
                    (long)(FileTransferControlReceiveStallRecoveryBroadcastCooldown - elapsed).TotalMilliseconds);
                return true;
            }
        }

        return false;
    }

    private void MarkFileTransferControlReceiveStallRecoveryBroadcasted()
        => Volatile.Write(ref fileTransferControlReceiveStallRecoveryBroadcastLastTick, Stopwatch.GetTimestamp());

    private void ClearUnresolvedFileTransferV6TransportEpochs(string reason)
    {
        int clearedCount;
        lock (fileTransferV6TransportEpochGate)
        {
            clearedCount = unresolvedFileTransferV6TransportEpochs.Count;
            unresolvedFileTransferV6TransportEpochs.Clear();
            lastRecoveredFileTransferV6RegularNknEpoch = null;
        }

        if (clearedCount > 0)
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=filetransfer_v6_epoch_observer_cleared; reason={SanitizeLogToken(reason)}; cleared_count={clearedCount}");
        }
    }

    private void RebindFileTransferDataSessionsForTunaFallback(
        string reason,
        string? sessionId,
        NknAccelerationLaneKind lanes)
    {
        if ((lanes & NknAccelerationLaneKind.File) != NknAccelerationLaneKind.File ||
            string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var normalizedReason = SanitizeLogToken(reason);
        ClearPendingFileTransferV6Handoffs("tuna_fallback", sessionId);
        if (ShouldSuppressDuplicateRecoveredFileTransferFallback(sessionId, normalizedReason))
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=filetransfer_v6_fallback_handoff_suppressed_duplicate; session_id={SanitizeLogToken(sessionId ?? "none")}; reason={normalizedReason}; target_transport={FormatFileTransferTransportKindForLog(FileTransferTransportKind.RegularNkn)}");
            return;
        }

        MarkTunaFallbackLaneState(
            sessionId,
            lane: NknAccelerationLaneKind.File,
            state: TunaFallbackLaneState.Pending,
            reason: normalizedReason);
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_fallback_filetransfer_rebind_requested; session_id={sessionId}; reason={normalizedReason}; lanes={FormatAccelerationLanesForLog(lanes)}");
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_disable_handoff_started; session_id={sessionId}; reason={normalizedReason}; lanes={FormatAccelerationLanesForLog(lanes)}");
        MarkFileTransferFallbackNknProofPending(normalizedReason, sessionId, lanes);
        SetFileTransferDataSessionsAvailability(
            isAvailable: false,
            reason: normalizedReason,
            requiresResumeRequest: true,
            handoffKind: FileTransferTransportHandoffKind.TunaToNormalFallback,
            targetTransport: FileTransferTransportKind.RegularNkn);
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_disable_handoff_nkn_pending; session_id={sessionId}; reason={normalizedReason}; lanes={FormatAccelerationLanesForLog(lanes)}");

        if (ShouldStartImmediateFileTransferFallbackProbe(normalizedReason))
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=filetransfer_fallback_nkn_probe_started; session_id={sessionId}; reason={normalizedReason}; trigger=cap_handoff_immediate; delay_ms=0; lanes={FormatAccelerationLanesForLog(lanes)}");
            ArmPostTunaFallbackProofSendWindow(normalizedReason, "cap_handoff_immediate", sessionId);
            SetFileTransferDataSessionsAvailability(
                isAvailable: false,
                reason: "transport_recovered_unproven",
                requiresResumeRequest: true,
                handoffKind: FileTransferTransportHandoffKind.TunaToNormalFallback,
                targetTransport: FileTransferTransportKind.RegularNkn);
        }
    }

    private bool ShouldSuppressDuplicateRecoveredFileTransferFallback(string? sessionId, string reason)
    {
        lock (accelerationGate)
        {
            if (!TryGetCurrentTunaFallbackProofStateUnsafe(sessionId, out var current))
            {
                return false;
            }

            return IsFileTransferFallbackFinalForLanes(current, NknAccelerationLaneKind.File);
        }
    }

    private static bool IsFileTransferFallbackFinalForLanes(TunaFallbackProofState state, NknAccelerationLaneKind lanes)
        => (lanes & NknAccelerationLaneKind.File) == NknAccelerationLaneKind.File &&
           (state.Lanes & NknAccelerationLaneKind.File) == NknAccelerationLaneKind.File &&
           state.FileState == TunaFallbackLaneState.Recovered;

    private static bool ShouldReplaceFinalFileTransferFallbackForFreshUserStop(
        TunaFallbackProofState state,
        NknAccelerationLaneKind lanes,
        string normalizedReason)
        => (lanes & NknAccelerationLaneKind.File) == NknAccelerationLaneKind.File &&
           IsFileTransferFallbackFinalForLanes(state, lanes) &&
           (IsUserRequestedAccelerationStopReason(normalizedReason) ||
            IsRemoteUserRequestedAccelerationStopReason(normalizedReason));

    private void RequestFileTransferTunaActivationHandoff(
        string? sessionId,
        NknAccelerationLaneKind lanes,
        string reason)
    {
        if ((lanes & NknAccelerationLaneKind.File) != NknAccelerationLaneKind.File ||
            string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var normalizedReason = SanitizeLogToken(reason);
        SupersedePostTunaFileFallbackRouteForFileTunaActivation(sessionId, normalizedReason);
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_activation_filetransfer_handoff_requested; session_id={SanitizeLogToken(sessionId)}; reason={normalizedReason}; lanes={FormatAccelerationLanesForLog(lanes)}");
        ResumeFileTransferDataSessionsAfterTunaActivationNegotiation(
            "tuna_activation_negotiated_transport_ready",
            sessionId,
            normalizedReason);
        RequestFileTransferDataSessionsHandoff(
            normalizedReason,
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna,
            sessionId);
    }

    private void SupersedePostTunaFileFallbackRouteForFileTunaActivation(string sessionId, string reason)
    {
        TunaFallbackProofState? snapshot = null;
        TunaFallbackLaneState previousFileState = TunaFallbackLaneState.None;
        V6TransportEpochState? previousFileV6EpochState = null;
        long previousFileV6TransportEpoch = 0;

        lock (accelerationGate)
        {
            if (tunaFallbackProofState is { } state &&
                string.Equals(state.SessionId, sessionId, StringComparison.Ordinal) &&
                (state.Lanes & NknAccelerationLaneKind.File) == NknAccelerationLaneKind.File &&
                state.FileState != TunaFallbackLaneState.None)
            {
                snapshot = state;
                previousFileState = state.FileState;
                previousFileV6EpochState = state.FileV6EpochState;
                previousFileV6TransportEpoch = state.FileV6TransportEpoch;
            }
        }

        if (snapshot is not null)
        {
            var previousFileV6EpochStateToken = previousFileV6EpochState is { } epochState
                ? FormatFileTransferV6TransportEpochStateForLog(epochState)
                : "none";
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=filetransfer_post_tuna_fallback_v6_route_superseded; session_id={SanitizeLogToken(sessionId)}; fallback_epoch={snapshot.Epoch}; reason={SanitizeLogToken(reason)}; previous_file_state={FormatTunaFallbackLaneState(previousFileState)}; previous_file_v6_epoch_state={SanitizeLogToken(previousFileV6EpochStateToken)}; previous_file_v6_transport_epoch={previousFileV6TransportEpoch}; next_file_route=file_tuna_v4");
        }

        CompleteTunaFallbackProof("tuna_activation_started");
    }

    private bool PauseFileTransferDataSessionsForTunaActivationNegotiation(
        string reason,
        string? sessionId,
        string trigger)
    {
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId)
            ? currentSessionSecurityState.SessionId?.Value
            : sessionId.Trim();
        if (string.IsNullOrWhiteSpace(normalizedSessionId))
        {
            return false;
        }

        lock (accelerationGate)
        {
            if (IsAccelerationNegotiatedAndHealthyUnsafe(normalizedSessionId))
            {
                return false;
            }

            if (string.Equals(fileTransferTunaActivationPauseSessionId, normalizedSessionId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (ShouldKeepPostTunaFallbackDataSessionsAvailableDuringTunaActivation(
                normalizedSessionId,
                out var suppressReason))
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=filetransfer_tuna_activation_negotiation_post_tuna_fallback_pause_suppressed; session_id={SanitizeLogToken(normalizedSessionId)}; reason={SanitizeLogToken(reason)}; trigger={SanitizeLogToken(trigger)}; suppress_reason={SanitizeLogToken(suppressReason)}");
            return false;
        }

        long generation;
        lock (accelerationGate)
        {
            if (IsAccelerationNegotiatedAndHealthyUnsafe(normalizedSessionId))
            {
                return false;
            }

            if (string.Equals(fileTransferTunaActivationPauseSessionId, normalizedSessionId, StringComparison.Ordinal))
            {
                return false;
            }

            fileTransferTunaActivationPauseSessionId = normalizedSessionId;
            generation = ++fileTransferTunaActivationPauseGeneration;
        }

        var normalizedReason = SanitizeLogToken(reason);
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=filetransfer_tuna_activation_negotiation_regular_nkn_paused; session_id={SanitizeLogToken(normalizedSessionId)}; reason={normalizedReason}; trigger={SanitizeLogToken(trigger)}; max_pause_ms={(long)FileTransferTunaActivationPauseMax.TotalMilliseconds}");
        SetFileTransferDataSessionsAvailability(
            isAvailable: false,
            reason: "tuna_activation_negotiating",
            requiresResumeRequest: false,
            handoffKind: FileTransferTransportHandoffKind.None,
            targetTransport: FileTransferTransportKind.RegularNkn);
        ScheduleFileTransferTunaActivationPauseExpiry(normalizedSessionId, generation);
        return true;
    }

    private bool ShouldKeepPostTunaFallbackDataSessionsAvailableDuringTunaActivation(
        string sessionId,
        out string reason)
    {
        if (HasActivePostTunaFallbackFileTransferRouteHint(sessionId))
        {
            reason = "active_post_tuna_fallback_route";
            return true;
        }

        if (TryGetUnresolvedFileTransferV6TransportEpochForCurrentSession(out var pendingEpoch) &&
            string.Equals(pendingEpoch.SessionId, sessionId, StringComparison.Ordinal) &&
            IsPostTunaFallbackRegularNknEpoch(pendingEpoch))
        {
            reason = "unresolved_post_tuna_fallback_v6_epoch";
            return true;
        }

        lock (accelerationGate)
        {
            if (TryGetCurrentTunaFallbackProofStateUnsafe(sessionId, out var state) &&
                (state.Lanes & NknAccelerationLaneKind.File) == NknAccelerationLaneKind.File &&
                state.FileState != TunaFallbackLaneState.None)
            {
                reason = "active_post_tuna_fallback_state";
                return true;
            }
        }

        reason = "none";
        return false;
    }

    private void ResumeFileTransferDataSessionsAfterTunaActivationNegotiation(
        string reason,
        string? sessionId,
        string trigger)
        => _ = TryResumeFileTransferDataSessionsAfterTunaActivationNegotiation(reason, sessionId, trigger);

    private bool TryResumeFileTransferDataSessionsAfterTunaActivationNegotiation(
        string reason,
        string? sessionId,
        string trigger)
    {
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId)
            ? currentSessionSecurityState.SessionId?.Value
            : sessionId.Trim();
        if (string.IsNullOrWhiteSpace(normalizedSessionId))
        {
            return false;
        }

        lock (accelerationGate)
        {
            if (!string.Equals(fileTransferTunaActivationPauseSessionId, normalizedSessionId, StringComparison.Ordinal))
            {
                return false;
            }

            fileTransferTunaActivationPauseSessionId = null;
            fileTransferTunaActivationPauseGeneration++;
        }

        var normalizedReason = SanitizeLogToken(reason);
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=filetransfer_tuna_activation_negotiation_regular_nkn_resumed; session_id={SanitizeLogToken(normalizedSessionId)}; reason={normalizedReason}; trigger={SanitizeLogToken(trigger)}");
        SetFileTransferDataSessionsAvailability(
            isAvailable: true,
            reason: normalizedReason,
            requiresResumeRequest: false,
            handoffKind: FileTransferTransportHandoffKind.None,
            targetTransport: FileTransferTransportKind.RegularNkn);
        return true;
    }

    private void ResumeFileTransferDataSessionsAfterTunaActivationFailure(
        string failureReason,
        string? sessionId,
        string trigger)
    {
        var normalizedFailureReason = SanitizeLogToken(failureReason);
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId)
            ? currentSessionSecurityState.SessionId?.Value
            : sessionId.Trim();
        if (!TryResumeFileTransferDataSessionsAfterTunaActivationNegotiation(
                "tuna_activation_failed_regular_v4_resumed",
                normalizedSessionId,
                normalizedFailureReason))
        {
            return;
        }

        LocalOperationalLog.Warn(
            "NKN.Tuna",
            $"event=tuna_activation_failed_regular_v4_resumed; session_id={SanitizeLogToken(normalizedSessionId ?? "none")}; failure_reason={normalizedFailureReason}; trigger={SanitizeLogToken(trigger)}");
    }

    private bool ShouldSuppressFileTransferTransportRecoveredForTunaActivationPause(
        string trigger,
        out string? sessionId)
    {
        sessionId = currentSessionSecurityState.SessionId?.Value;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        lock (accelerationGate)
        {
            if (!string.Equals(fileTransferTunaActivationPauseSessionId, sessionId, StringComparison.Ordinal) ||
                IsAccelerationNegotiatedAndHealthyUnsafe(sessionId))
            {
                return false;
            }
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=filetransfer_tuna_activation_negotiation_transport_recovered_suppressed; session_id={SanitizeLogToken(sessionId)}; reason=tuna_activation_negotiating; trigger={SanitizeLogToken(trigger)}");
        return true;
    }

    private bool TryGetActiveFileTransferTunaActivationPauseForCurrentSession(out string? sessionId)
        => TryGetActiveFileTransferTunaActivationPause(
            currentSessionSecurityState.SessionId?.Value,
            out sessionId);

    private bool TryGetActiveFileTransferTunaActivationPause(
        string? requestedSessionId,
        out string? sessionId)
    {
        sessionId = string.IsNullOrWhiteSpace(requestedSessionId)
            ? currentSessionSecurityState.SessionId?.Value
            : requestedSessionId.Trim();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        lock (accelerationGate)
        {
            return string.Equals(fileTransferTunaActivationPauseSessionId, sessionId, StringComparison.Ordinal) &&
                   !IsAccelerationNegotiatedAndHealthyUnsafe(sessionId);
        }
    }

    private void MarkFileTransferTunaActivationBridgeRecoveryStarted(string reason)
    {
        lock (accelerationBridgeRecoveryGate)
        {
            fileTransferTunaActivationBridgeRecoveryStartedTick = Stopwatch.GetTimestamp();
            fileTransferTunaActivationBridgeRecoveryActive = 1;
            if (fileTransferTunaActivationBridgeRecoverySettledTcs is null ||
                fileTransferTunaActivationBridgeRecoverySettledTcs.Task.IsCompleted)
            {
                fileTransferTunaActivationBridgeRecoverySettledTcs =
                    new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    private void MarkFileTransferTunaActivationBridgeRecoverySettled(string trigger)
    {
        TaskCompletionSource<bool>? settledTcs = null;
        long startedTick;
        lock (accelerationBridgeRecoveryGate)
        {
            if (fileTransferTunaActivationBridgeRecoveryActive == 0)
            {
                return;
            }

            fileTransferTunaActivationBridgeRecoveryActive = 0;
            fileTransferTunaActivationBridgeRecoverySettledTick = Stopwatch.GetTimestamp();
            startedTick = fileTransferTunaActivationBridgeRecoveryStartedTick;
            settledTcs = fileTransferTunaActivationBridgeRecoverySettledTcs;
        }

        settledTcs?.TrySetResult(true);
        ScheduleRuntimeUnlockRetryAfterRecoveryIfArmed(trigger);
    }

    private bool IsFileTransferTunaActivationBridgeRecoveryActive()
    {
        lock (accelerationBridgeRecoveryGate)
        {
            return fileTransferTunaActivationBridgeRecoveryActive != 0;
        }
    }

    private async Task<bool> WaitForFileTransferTunaActivationBridgeRecoveryBeforeControlSendAsync(
        string purpose,
        string? activationSessionId,
        CancellationToken ct)
    {
        if (!IsTunaActivationOfferSendPurpose(purpose) ||
            !TryGetFileTransferTunaActivationBridgeRecoveryWaitSession(purpose, activationSessionId, out var sessionId))
        {
            return true;
        }

        var waitBudget = GetFileTransferTunaActivationBridgeRecoveryControlSendWaitBudget(
            purpose,
            activationSessionId,
            FileTransferTunaActivationBridgeRecoveryWaitOverrideForTests ??
            FileTransferTunaActivationBridgeRecoveryWait);
        var waitStartedTick = Stopwatch.GetTimestamp();
        var loggedWait = false;
        var loggedRegularV4PressureBypass = false;
        var loggedRegularV4ReceiveStallWait = false;
        string? lastBlockerReason = null;
        long lastBlockerRemainingMs = 0;
        long bridgeRecoveryStartedTick = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            Task? waitTask = null;
            var bridgeRecoveryActive = false;
            lock (accelerationBridgeRecoveryGate)
            {
                if (fileTransferTunaActivationBridgeRecoveryActive != 0 &&
                    fileTransferTunaActivationBridgeRecoverySettledTcs is not null)
                {
                    bridgeRecoveryActive = true;
                    waitTask = fileTransferTunaActivationBridgeRecoverySettledTcs.Task;
                    bridgeRecoveryStartedTick = fileTransferTunaActivationBridgeRecoveryStartedTick;
                }
            }

            var regularV4BlockerReason = string.Empty;
            var regularV4BlockerRemainingMs = 0L;
            var realClient = client as RealNknClientAdapter;
            var hasRegularV4SendBlocker =
                realClient is not null &&
                realClient.TryGetFileTransferRegularV4ActivationSendBlocker(
                    out regularV4BlockerReason,
                    out regularV4BlockerRemainingMs);
            if (hasRegularV4SendBlocker &&
                realClient is not null &&
                IsCurrentRuntimeUnlockActivationOffer() &&
                !TryGetActiveFileTransferTunaActivationPause(sessionId, out _) &&
                realClient.TryGetFileTransferRegularV4ActivationSendBlocker(
                    out var nonPressureBlockerReason,
                    out var nonPressureBlockerRemainingMs,
                    includeRegularV4Pressure: false))
            {
                regularV4BlockerReason = nonPressureBlockerReason;
                regularV4BlockerRemainingMs = nonPressureBlockerRemainingMs;
                if (!loggedRegularV4ReceiveStallWait)
                {
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_activation_control_send_waiting_for_regular_v4_recovery; session_id={SanitizeLogToken(sessionId ?? "none")}; purpose={SanitizeLogToken(purpose)}; blocker_reason={SanitizeLogToken(nonPressureBlockerReason)}; blocker_remaining_ms={nonPressureBlockerRemainingMs}; regular_v4_pressure_reason={SanitizeLogToken(regularV4BlockerReason)}; regular_v4_pressure_remaining_ms={regularV4BlockerRemainingMs}; recovery_age_ms={FormatElapsedMilliseconds(bridgeRecoveryStartedTick)}; wait_budget_ms={(long)waitBudget.TotalMilliseconds}; reason=runtime_unlock_regular_v4_receive_stall");
                    loggedRegularV4ReceiveStallWait = true;
                }
            }

            if (hasRegularV4SendBlocker &&
                TryGetActiveFileTransferTunaActivationPause(sessionId, out _) &&
                IsRegularV4PressureActivationSendBlocker(regularV4BlockerReason))
            {
                if (!loggedRegularV4PressureBypass)
                {
                    LocalOperationalLog.Info(
                        "NKN.Tuna",
                        $"event=tuna_activation_control_send_regular_v4_pressure_bypassed; session_id={SanitizeLogToken(sessionId ?? "none")}; purpose={SanitizeLogToken(purpose)}; blocker_reason={SanitizeLogToken(regularV4BlockerReason)}; blocker_remaining_ms={regularV4BlockerRemainingMs}; reason=activation_pause_active");
                    loggedRegularV4PressureBypass = true;
                }

                hasRegularV4SendBlocker = false;
                regularV4BlockerReason = string.Empty;
                regularV4BlockerRemainingMs = 0;
            }

            if (hasRegularV4SendBlocker &&
                ShouldBypassRegularV4ReceiveStallForRuntimeUnlockAuthorityProbe(
                    regularV4BlockerReason,
                    sessionId))
            {
                return true;
            }

            if (hasRegularV4SendBlocker &&
                ShouldBypassPostTunaFallbackReceiveStallForRuntimeUnlockAuthorityProbe(
                    regularV4BlockerReason,
                    sessionId,
                    regularV4BlockerRemainingMs,
                    allowStaleInProgressAuthorityProbe: false))
            {
                return true;
            }

            if (hasRegularV4SendBlocker)
            {
                lastBlockerReason = regularV4BlockerReason;
                lastBlockerRemainingMs = regularV4BlockerRemainingMs;
            }
            else if (bridgeRecoveryActive)
            {
                lastBlockerReason = "bridge_recovery_active";
                lastBlockerRemainingMs = 0;
            }

            if (!bridgeRecoveryActive && !hasRegularV4SendBlocker)
            {
                if (loggedWait)
                {
                    LocalOperationalLog.Info(
                        "NKN.Tuna",
                        $"event=tuna_activation_control_send_bridge_recovery_settled; session_id={SanitizeLogToken(sessionId ?? "none")}; purpose={SanitizeLogToken(purpose)}; recovery_age_ms={FormatElapsedMilliseconds(bridgeRecoveryStartedTick)}; blocker_reason={SanitizeLogToken(lastBlockerReason ?? "cleared")}");
                }

                return true;
            }

            if (!loggedWait)
            {
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=tuna_activation_control_send_waiting_for_bridge_recovery; session_id={SanitizeLogToken(sessionId ?? "none")}; purpose={SanitizeLogToken(purpose)}; recovery_age_ms={FormatElapsedMilliseconds(bridgeRecoveryStartedTick)}; wait_budget_ms={(long)waitBudget.TotalMilliseconds}; blocker_reason={SanitizeLogToken(lastBlockerReason ?? "unknown")}; blocker_remaining_ms={lastBlockerRemainingMs}");
                loggedWait = true;
            }

            var elapsed = Stopwatch.GetElapsedTime(waitStartedTick);
            var remaining = waitBudget - elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                if (ShouldBypassRegularV4PressureForRuntimeUnlockOffer(lastBlockerReason))
                {
                    LocalOperationalLog.Info(
                        "NKN.Tuna",
                        $"event=tuna_activation_control_send_regular_v4_pressure_bypassed; session_id={SanitizeLogToken(sessionId ?? "none")}; purpose={SanitizeLogToken(purpose)}; blocker_reason={SanitizeLogToken(lastBlockerReason ?? "unknown")}; blocker_remaining_ms={lastBlockerRemainingMs}; recovery_age_ms={FormatElapsedMilliseconds(bridgeRecoveryStartedTick)}; wait_budget_ms={(long)waitBudget.TotalMilliseconds}; reason=runtime_unlock_offer_pressure_wait_elapsed");
                    return true;
                }

                if (ShouldBypassRegularV4ReceiveStallForRuntimeUnlockObservedOfferReplay(
                        lastBlockerReason,
                        sessionId,
                        purpose))
                {
                    LocalOperationalLog.Info(
                        "NKN.Tuna",
                        $"event=tuna_activation_control_send_regular_v4_receive_stall_observed_replay_bypassed; session_id={SanitizeLogToken(sessionId ?? "none")}; purpose={SanitizeLogToken(purpose)}; blocker_reason={SanitizeLogToken(lastBlockerReason ?? "unknown")}; blocker_remaining_ms={lastBlockerRemainingMs}; recovery_age_ms={FormatElapsedMilliseconds(bridgeRecoveryStartedTick)}; wait_budget_ms={(long)waitBudget.TotalMilliseconds}; reason=runtime_unlock_observed_offer_replay_window");
                    return true;
                }

                if (ShouldBypassPostTunaFallbackReceiveStallForRuntimeUnlockObservedOfferReplay(
                        lastBlockerReason,
                        sessionId,
                        purpose,
                        lastBlockerRemainingMs))
                {
                    return true;
                }

                if (ShouldBypassRegularV4ReceiveStallForRuntimeUnlockOffer(
                        lastBlockerReason,
                        sessionId,
                        lastBlockerRemainingMs,
                        allowStaleInProgressAuthorityProbe: true))
                {
                    TryGrantRuntimeUnlockRecoveryContractRetryAuthorityForReceiveStallBypass(
                        sessionId,
                        purpose,
                        lastBlockerReason);
                    LocalOperationalLog.Info(
                        "NKN.Tuna",
                        $"event=tuna_activation_control_send_regular_v4_receive_stall_bypassed; session_id={SanitizeLogToken(sessionId ?? "none")}; purpose={SanitizeLogToken(purpose)}; blocker_reason={SanitizeLogToken(lastBlockerReason ?? "unknown")}; blocker_remaining_ms={lastBlockerRemainingMs}; recovery_age_ms={FormatElapsedMilliseconds(bridgeRecoveryStartedTick)}; wait_budget_ms={(long)waitBudget.TotalMilliseconds}; reason=runtime_unlock_offer_receive_stall_wait_elapsed");
                    return true;
                }

                if (ShouldBypassPostTunaFallbackReceiveStallForRuntimeUnlockAuthorityProbe(
                        lastBlockerReason,
                        sessionId,
                        lastBlockerRemainingMs,
                        allowStaleInProgressAuthorityProbe: true))
                {
                    return true;
                }

                if (ShouldDeferPostTunaFallbackReceiveStallForRuntimeUnlockOffer(lastBlockerReason, sessionId))
                {
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_activation_control_send_post_tuna_fallback_recovery_deferred; session_id={SanitizeLogToken(sessionId ?? "none")}; purpose={SanitizeLogToken(purpose)}; blocker_reason={SanitizeLogToken(lastBlockerReason ?? "unknown")}; blocker_remaining_ms={lastBlockerRemainingMs}; recovery_age_ms={FormatElapsedMilliseconds(bridgeRecoveryStartedTick)}; wait_budget_ms={(long)waitBudget.TotalMilliseconds}; reason=runtime_unlock_offer_waiting_for_post_tuna_fallback_receive_proof");
                    return false;
                }

                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=tuna_activation_control_send_deferred_for_regular_v4_recovery; session_id={SanitizeLogToken(sessionId ?? "none")}; purpose={SanitizeLogToken(purpose)}; blocker_reason={SanitizeLogToken(lastBlockerReason ?? "unknown")}; blocker_remaining_ms={lastBlockerRemainingMs}; recovery_age_ms={FormatElapsedMilliseconds(bridgeRecoveryStartedTick)}; wait_budget_ms={(long)waitBudget.TotalMilliseconds}");
                return false;
            }

            var pollDelay = TimeSpan.FromMilliseconds(Math.Min(250, Math.Max(1, remaining.TotalMilliseconds)));
            if (waitTask is not null)
            {
                var completed = await Task.WhenAny(waitTask, Task.Delay(pollDelay, ct)).ConfigureAwait(false);
                if (ReferenceEquals(completed, waitTask))
                {
                    continue;
                }
            }
            else
            {
                await Task.Delay(pollDelay, ct).ConfigureAwait(false);
            }
        }
    }

    private TimeSpan GetFileTransferTunaActivationBridgeRecoveryControlSendWaitBudget(
        string purpose,
        string? sessionId,
        TimeSpan baseBudget)
    {
        if (TryGetFileTransferTunaActivationBridgeRecoveryControlSendWaitExtension(
            purpose,
            sessionId,
            baseBudget,
            out var extendedBudget,
            out var blockerReason))
        {
            return extendedBudget;
        }

        return baseBudget;
    }

    private bool TryGetFileTransferTunaActivationBridgeRecoveryControlSendWaitExtension(
        string purpose,
        string? sessionId,
        TimeSpan baseBudget,
        out TimeSpan extendedBudget,
        out string blockerReason)
    {
        extendedBudget = baseBudget;
        blockerReason = "none";

        if (!IsTunaActivationOfferSendPurpose(purpose))
        {
            blockerReason = "not_activation_offer_send";
            return false;
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            blockerReason = "missing_session_id";
            return false;
        }

        var hasRuntimeUnlockOffer = IsCurrentRuntimeUnlockActivationOffer();
        if (!TryGetRuntimeUnlockRetryAuthorityForCurrentOffer(out var authorityState, sessionId) ||
            authorityState is null)
        {
            blockerReason = hasRuntimeUnlockOffer
                ? "missing_runtime_unlock_retry_authority"
                : "not_current_runtime_unlock_offer";
            return false;
        }

        if (!authorityState.RetryDispatched)
        {
            blockerReason = "retry_not_dispatched";
            return false;
        }

        if (!authorityState.RetryAuthorityPending)
        {
            blockerReason = "retry_authority_not_pending";
            return false;
        }

        if (!string.Equals(authorityState.SessionId, sessionId.Trim(), StringComparison.Ordinal))
        {
            blockerReason = "authority_session_mismatch";
            return false;
        }

        if (!TryGetRegularV4StartedRecoveryAuthorityProbeRemaining(sessionId, out var remainingMs, out var startedAgeMs))
        {
            blockerReason = "regular_v4_started_recovery_probe_not_pending";
            return false;
        }

        var requiredBudget = TimeSpan.FromMilliseconds(Math.Max(0, remainingMs) + 1_000);
        if (requiredBudget <= baseBudget)
        {
            blockerReason = "base_budget_sufficient";
            return false;
        }

        extendedBudget = requiredBudget;
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_activation_control_send_wait_budget_extended_for_regular_v4_authority_probe; session_id={SanitizeLogToken(sessionId)}; purpose={SanitizeLogToken(purpose)}; base_wait_budget_ms={(long)baseBudget.TotalMilliseconds}; extended_wait_budget_ms={(long)requiredBudget.TotalMilliseconds}; recovery_started_age_ms={startedAgeMs}; probe_remaining_ms={remainingMs}; contract_generation={authorityState.ContractGeneration}; authority_attempt={authorityState.AuthorityAttempt}; reason=awaiting_bounded_authority_probe_window");
        return true;
    }

    private bool ShouldBypassRegularV4PressureForRuntimeUnlockOffer(string? blockerReason)
    {
        if (!IsRegularV4PressureActivationSendBlocker(blockerReason) ||
            !IsCurrentRuntimeUnlockActivationOffer())
        {
            return false;
        }

        if (client is not RealNknClientAdapter realClient)
        {
            return false;
        }

        return !realClient.TryGetFileTransferRegularV4ActivationSendBlocker(
            out _,
            out _,
            includeRegularV4Pressure: false);
    }

    private bool ShouldBypassRegularV4ReceiveStallForRuntimeUnlockAuthorityProbe(
        string? blockerReason,
        string? sessionId)
    {
        if (!IsReceiveStallActivationSendBlocker(blockerReason) ||
            !IsCurrentRuntimeUnlockActivationOffer() ||
            string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var normalizedSessionId = SanitizeLogToken(sessionId);
        if (!HasActiveRegularV4FileTransferRouteHint(normalizedSessionId) ||
            HasActivePostTunaFallbackFileTransferRouteHint(normalizedSessionId) ||
            TryGetUnresolvedFileTransferV6TransportEpochForCurrentSession(out _))
        {
            return false;
        }

        if (TryGetRuntimeUnlockRetryAuthorityForCurrentOffer(out var authorityState) &&
            authorityState is { RetryDispatched: true, RetryAuthorityPending: true })
        {
            if (IsReceiveStallRecoveryInProgressActivationSendBlocker(blockerReason))
            {
                return false;
            }

            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_activation_control_send_regular_v4_receive_stall_authority_probe_allowed; session_id={SanitizeLogToken(normalizedSessionId)}; blocker_reason={SanitizeLogToken(blockerReason ?? "unknown")}; liveness_state=authority_send_window; contract_generation={authorityState.ContractGeneration}; authority_attempt={authorityState.AuthorityAttempt}; reason=bounded_authority_observed_send_probe");
            return true;
        }

        return false;
    }

    private bool ShouldBypassPostTunaFallbackReceiveStallForRuntimeUnlockAuthorityProbe(
        string? blockerReason,
        string? sessionId,
        long blockerRemainingMs,
        bool allowStaleInProgressAuthorityProbe)
    {
        if (!IsReceiveStallActivationSendBlocker(blockerReason) ||
            !IsCurrentRuntimeUnlockActivationOffer() ||
            string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var normalizedSessionId = SanitizeLogToken(sessionId);
        if (!HasActivePostTunaFallbackFileTransferRouteHint(normalizedSessionId))
        {
            return false;
        }

        if (!TryGetRuntimeUnlockRetryAuthorityForCurrentOffer(out var authorityState) ||
            authorityState is not { RetryDispatched: true, RetryAuthorityPending: true })
        {
            return false;
        }

        if (!TryGetCurrentPostTunaFallbackObservedSendProbeProof(
                normalizedSessionId,
                out var proof,
                out var proofDirection,
                out var proofAgeMs))
        {
            if (IsCurrentPostTunaFallbackAuthorityBridgeRecoveryInProgress(
                    normalizedSessionId,
                    out var bridgeRecoveryReason))
            {
                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=tuna_activation_control_send_post_tuna_fallback_receive_stall_bypass_blocked; session_id={SanitizeLogToken(normalizedSessionId)}; blocker_reason={SanitizeLogToken(blockerReason ?? "unknown")}; blocker_remaining_ms={blockerRemainingMs}; contract_generation={authorityState.ContractGeneration}; authority_attempt={authorityState.AuthorityAttempt}; reason={SanitizeLogToken(bridgeRecoveryReason)}");
                return false;
            }

            if (IsRuntimeUnlockPostTunaFallbackFinalProbeAuthoritySnapshot(
                    authorityState,
                    out var finalProbeDeadlineRemainingMs) &&
                (!IsReceiveStallRecoveryInProgressActivationSendBlocker(blockerReason) ||
                 blockerRemainingMs <= 0 ||
                 allowStaleInProgressAuthorityProbe))
            {
                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=tuna_activation_control_send_post_tuna_fallback_receive_stall_authority_probe_allowed; session_id={SanitizeLogToken(normalizedSessionId)}; blocker_reason={SanitizeLogToken(blockerReason ?? "unknown")}; blocker_remaining_ms={blockerRemainingMs}; contract_generation={authorityState.ContractGeneration}; authority_attempt={authorityState.AuthorityAttempt}; liveness_deadline_remaining_ms={finalProbeDeadlineRemainingMs}; reason=bounded_final_observed_send_probe_without_fallback_proof");
                return true;
            }

            if (TryGetCurrentPostTunaFallbackAuthorityProbeState(
                    normalizedSessionId,
                    out var authorityStable,
                    out var authorityReason) &&
                !authorityStable)
            {
                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=tuna_activation_control_send_post_tuna_fallback_receive_stall_bypass_blocked; session_id={SanitizeLogToken(normalizedSessionId)}; blocker_reason={SanitizeLogToken(blockerReason ?? "unknown")}; blocker_remaining_ms={blockerRemainingMs}; contract_generation={authorityState.ContractGeneration}; authority_attempt={authorityState.AuthorityAttempt}; reason={SanitizeLogToken(authorityReason)}");
                return false;
            }

            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_activation_control_send_post_tuna_fallback_receive_stall_bypass_blocked; session_id={SanitizeLogToken(normalizedSessionId)}; blocker_reason={SanitizeLogToken(blockerReason ?? "unknown")}; blocker_remaining_ms={blockerRemainingMs}; contract_generation={authorityState.ContractGeneration}; authority_attempt={authorityState.AuthorityAttempt}; reason=awaiting_current_fallback_receive_or_frontier_proof");
            return false;
        }

        if (IsReceiveStallRecoveryInProgressActivationSendBlocker(blockerReason) &&
            blockerRemainingMs > 0)
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_activation_control_send_post_tuna_fallback_receive_stall_bypass_blocked; session_id={SanitizeLogToken(normalizedSessionId)}; blocker_reason={SanitizeLogToken(blockerReason ?? "unknown")}; blocker_remaining_ms={blockerRemainingMs}; contract_generation={authorityState.ContractGeneration}; authority_attempt={authorityState.AuthorityAttempt}; proof={SanitizeLogToken(proof)}; proof_direction={SanitizeLogToken(proofDirection)}; proof_age_ms={proofAgeMs}; reason=awaiting_bridge_recovery_settle");
            return false;
        }

        if (IsReceiveStallRecoveryInProgressActivationSendBlocker(blockerReason) &&
            !allowStaleInProgressAuthorityProbe)
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_activation_control_send_post_tuna_fallback_receive_stall_authority_probe_allowed; session_id={SanitizeLogToken(normalizedSessionId)}; blocker_reason={SanitizeLogToken(blockerReason ?? "unknown")}; blocker_remaining_ms={blockerRemainingMs}; contract_generation={authorityState.ContractGeneration}; authority_attempt={authorityState.AuthorityAttempt}; proof={SanitizeLogToken(proof)}; proof_direction={SanitizeLogToken(proofDirection)}; proof_age_ms={proofAgeMs}; reason=current_fallback_proof_cleared_stale_recovery_gate");
            return true;
        }

        LocalOperationalLog.Warn(
            "NKN.Tuna",
            $"event=tuna_activation_control_send_post_tuna_fallback_receive_stall_authority_probe_allowed; session_id={SanitizeLogToken(normalizedSessionId)}; blocker_reason={SanitizeLogToken(blockerReason ?? "unknown")}; blocker_remaining_ms={blockerRemainingMs}; contract_generation={authorityState.ContractGeneration}; authority_attempt={authorityState.AuthorityAttempt}; proof={SanitizeLogToken(proof)}; proof_direction={SanitizeLogToken(proofDirection)}; proof_age_ms={proofAgeMs}; reason=bounded_authority_observed_send_probe_after_wait");
        return true;
    }

    private bool ShouldBypassRegularV4ReceiveStallForRuntimeUnlockOffer(string? blockerReason, string? sessionId)
        => ShouldBypassRegularV4ReceiveStallForRuntimeUnlockOffer(
            blockerReason,
            sessionId,
            blockerRemainingMs: 0,
            allowStaleInProgressAuthorityProbe: false);

    private bool ShouldBypassRegularV4ReceiveStallForRuntimeUnlockOffer(
        string? blockerReason,
        string? sessionId,
        long blockerRemainingMs,
        bool allowStaleInProgressAuthorityProbe)
    {
        if (!IsReceiveStallActivationSendBlocker(blockerReason) ||
            !IsCurrentRuntimeUnlockActivationOffer() ||
            string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var normalizedSessionId = SanitizeLogToken(sessionId);
        if (!HasActiveRegularV4FileTransferRouteHint(normalizedSessionId) ||
            HasActivePostTunaFallbackFileTransferRouteHint(normalizedSessionId) ||
            TryGetUnresolvedFileTransferV6TransportEpochForCurrentSession(out _))
        {
            return false;
        }

        var hasAuthority = TryGetRuntimeUnlockRetryAuthorityForCurrentOffer(out var authorityState) &&
                           authorityState is { RetryDispatched: true, RetryAuthorityPending: true };
        var hasLivenessStatus = TryGetActiveRegularV4RecoveryLivenessStatus(
            normalizedSessionId,
            out var receiveProofObserved,
            out var terminal,
            out var deadlineExpired,
            out var stateReason,
            out var deadlineRemainingMs);

        if (IsReceiveStallRecoveryInProgressActivationSendBlocker(blockerReason))
        {
            if (hasLivenessStatus && receiveProofObserved && !terminal && !deadlineExpired)
            {
                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=tuna_activation_control_send_regular_v4_receive_stall_authority_probe_allowed; session_id={SanitizeLogToken(normalizedSessionId)}; blocker_reason={SanitizeLogToken(blockerReason ?? "unknown")}; blocker_remaining_ms={blockerRemainingMs}; allow_stale_in_progress_authority_probe={(allowStaleInProgressAuthorityProbe ? 1 : 0)}; liveness_state={SanitizeLogToken(stateReason)}; liveness_deadline_remaining_ms={deadlineRemainingMs}; contract_generation={(authorityState?.ContractGeneration ?? 0)}; authority_attempt={(authorityState?.AuthorityAttempt ?? 0)}; reason=validated_regular_v4_receive_proof_cleared_stale_recovery_gate");
                return true;
            }

            if (allowStaleInProgressAuthorityProbe &&
                hasAuthority &&
                TryGetRegularV4StartedRecoveryAuthorityProbeReady(normalizedSessionId, out var startedAgeMs))
            {
                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=tuna_activation_control_send_regular_v4_receive_stall_authority_probe_allowed; session_id={SanitizeLogToken(normalizedSessionId)}; blocker_reason={SanitizeLogToken(blockerReason ?? "unknown")}; blocker_remaining_ms={blockerRemainingMs}; allow_stale_in_progress_authority_probe=1; liveness_state={SanitizeLogToken(stateReason)}; recovery_started_age_ms={startedAgeMs}; contract_generation={authorityState!.ContractGeneration}; authority_attempt={authorityState.AuthorityAttempt}; reason=stale_bridge_recovery_bounded_authority_observed_send_probe");
                return true;
            }

            if (hasAuthority)
            {
                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=tuna_activation_control_send_regular_v4_receive_stall_bypass_blocked; session_id={SanitizeLogToken(normalizedSessionId)}; blocker_reason={SanitizeLogToken(blockerReason ?? "unknown")}; blocker_remaining_ms={blockerRemainingMs}; allow_stale_in_progress_authority_probe={(allowStaleInProgressAuthorityProbe ? 1 : 0)}; liveness_state={(hasLivenessStatus ? SanitizeLogToken(stateReason) : "bridge_recovery_in_progress")}; liveness_deadline_remaining_ms={(hasLivenessStatus ? deadlineRemainingMs : 0)}; contract_generation={authorityState!.ContractGeneration}; authority_attempt={authorityState.AuthorityAttempt}; reason=awaiting_bridge_recovery_settle_before_authority_probe");
                return false;
            }

            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_activation_control_send_regular_v4_receive_stall_bypass_blocked; session_id={SanitizeLogToken(normalizedSessionId)}; blocker_reason={SanitizeLogToken(blockerReason ?? "unknown")}; liveness_state={(hasLivenessStatus ? SanitizeLogToken(stateReason) : "bridge_recovery_in_progress")}; reason=awaiting_bridge_recovery_settle");
            return false;
        }

        if (!hasLivenessStatus)
        {
            return true;
        }

        if (receiveProofObserved)
        {
            return true;
        }

        if (!deadlineExpired &&
            deadlineRemainingMs <= (long)RuntimeUnlockRegularV4FinalObservedSendProbeWindow.TotalMilliseconds)
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_activation_control_send_regular_v4_receive_stall_final_probe_allowed; session_id={SanitizeLogToken(normalizedSessionId)}; blocker_reason={SanitizeLogToken(blockerReason ?? "unknown")}; liveness_state={SanitizeLogToken(stateReason)}; liveness_deadline_remaining_ms={deadlineRemainingMs}; reason=bounded_final_observed_send_probe");
            return true;
        }

        LocalOperationalLog.Warn(
            "NKN.Tuna",
            $"event=tuna_activation_control_send_regular_v4_receive_stall_bypass_blocked; session_id={SanitizeLogToken(normalizedSessionId)}; blocker_reason={SanitizeLogToken(blockerReason ?? "unknown")}; liveness_state={SanitizeLogToken(stateReason)}; reason=awaiting_validated_filetransfer_receive_proof");
        return false;
    }

    private bool TryGetRegularV4StartedRecoveryAuthorityProbeReady(
        string sessionId,
        out long startedAgeMs)
    {
        startedAgeMs = -1;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var normalizedSessionId = SanitizeLogToken(sessionId);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (fileTransferFallbackProofGate)
        {
            var state = fileTransferRegularV4RecoveryLivenessState;
            if (state is null ||
                !string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal) ||
                !string.Equals(state.RouteToken, FileTransferRouteResolver.RegularNknV4FastToken, StringComparison.Ordinal) ||
                state.ProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
                !IsRegularV4BridgeRecoveryStartedRuntimeUnlockProbeReadyUnsafe(state, nowMs))
            {
                return false;
            }

            startedAgeMs = Math.Max(0, nowMs - state.BridgeRecoveryStartedUtcMs);
            return true;
        }
    }

    private bool TryGetRegularV4StartedRecoveryAuthorityProbeRemaining(
        string sessionId,
        out long remainingMs,
        out long startedAgeMs)
    {
        remainingMs = -1;
        startedAgeMs = -1;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var normalizedSessionId = SanitizeLogToken(sessionId);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (fileTransferFallbackProofGate)
        {
            var state = fileTransferRegularV4RecoveryLivenessState;
            if (state is null ||
                !string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal) ||
                !string.Equals(state.RouteToken, FileTransferRouteResolver.RegularNknV4FastToken, StringComparison.Ordinal) ||
                state.ProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
                !state.BridgeRecoveryStarted ||
                state.BridgeRecoveryCompleted ||
                state.ReceiveProofObserved ||
                state.RecoveryExhausted ||
                state.Completed ||
                state.BridgeRecoveryStartedUtcMs <= 0)
            {
                return false;
            }

            var probeDelay = RuntimeUnlockRegularV4BridgeRecoveryStartedAuthorityProbeDelayOverrideForTests ??
                             RuntimeUnlockRegularV4BridgeRecoveryStartedAuthorityProbeDelay;
            startedAgeMs = Math.Max(0, nowMs - state.BridgeRecoveryStartedUtcMs);
            remainingMs = Math.Max(0, (long)probeDelay.TotalMilliseconds - startedAgeMs);
            return true;
        }
    }

    private bool ShouldBypassRegularV4ReceiveStallForRuntimeUnlockObservedOfferReplay(
        string? blockerReason,
        string? sessionId,
        string purpose)
    {
        if (!string.Equals(purpose, "offer_replay", StringComparison.OrdinalIgnoreCase) ||
            !IsReceiveStallActivationSendBlocker(blockerReason) ||
            !IsCurrentRuntimeUnlockActivationOffer() ||
            string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var normalizedSessionId = SanitizeLogToken(sessionId);
        if (!HasActiveRegularV4FileTransferRouteHint(normalizedSessionId) ||
            HasActivePostTunaFallbackFileTransferRouteHint(normalizedSessionId) ||
            TryGetUnresolvedFileTransferV6TransportEpochForCurrentSession(out _))
        {
            return false;
        }

        if (TryGetRuntimeUnlockObservedOfferReplayWindowForCurrentOffer(out var stateSnapshot, out _))
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_activation_control_send_regular_v4_receive_stall_observed_replay_allowed; session_id={SanitizeLogToken(normalizedSessionId)}; blocker_reason={SanitizeLogToken(blockerReason ?? "unknown")}; contract_generation={stateSnapshot!.ContractGeneration}; offer_generation={stateSnapshot.CurrentOfferGeneration}; observed_lane={SanitizeLogToken(stateSnapshot.AuthorizedObservedLane)}; reason=bounded_observed_offer_replay");
            return true;
        }

        return false;
    }

    private bool ShouldBypassPostTunaFallbackReceiveStallForRuntimeUnlockObservedOfferReplay(
        string? blockerReason,
        string? sessionId,
        string purpose,
        long blockerRemainingMs)
    {
        if (!string.Equals(purpose, "offer_replay", StringComparison.OrdinalIgnoreCase) ||
            !IsReceiveStallActivationSendBlocker(blockerReason) ||
            !IsCurrentRuntimeUnlockActivationOffer() ||
            string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var normalizedSessionId = sessionId.Trim();
        if (!HasActivePostTunaFallbackFileTransferRouteHint(normalizedSessionId))
        {
            return false;
        }

        if (!TryGetRuntimeUnlockObservedOfferReplayWindowForCurrentOffer(out var stateSnapshot, out _))
        {
            return false;
        }

        if (!TryGetCurrentPostTunaFallbackObservedSendProbeProof(
                normalizedSessionId,
                out var proof,
                out var proofDirection,
                out var proofAgeMs))
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_activation_control_send_post_tuna_fallback_observed_replay_blocked; session_id={SanitizeLogToken(normalizedSessionId)}; blocker_reason={SanitizeLogToken(blockerReason ?? "unknown")}; blocker_remaining_ms={blockerRemainingMs}; contract_generation={stateSnapshot!.ContractGeneration}; offer_generation={stateSnapshot.CurrentOfferGeneration}; observed_lane={SanitizeLogToken(stateSnapshot.AuthorizedObservedLane)}; reason=awaiting_current_fallback_receive_or_frontier_proof");
            return false;
        }

        LocalOperationalLog.Warn(
            "NKN.Tuna",
            $"event=tuna_activation_control_send_post_tuna_fallback_receive_stall_observed_replay_allowed; session_id={SanitizeLogToken(normalizedSessionId)}; blocker_reason={SanitizeLogToken(blockerReason ?? "unknown")}; blocker_remaining_ms={blockerRemainingMs}; contract_generation={stateSnapshot!.ContractGeneration}; offer_generation={stateSnapshot.CurrentOfferGeneration}; observed_lane={SanitizeLogToken(stateSnapshot.AuthorizedObservedLane)}; proof={SanitizeLogToken(proof)}; proof_direction={SanitizeLogToken(proofDirection)}; proof_age_ms={proofAgeMs}; reason=bounded_observed_offer_replay");
        return true;
    }

    private void ArmRuntimeUnlockQueueAcceptedObservedEscape(string reason)
    {
        lock (accelerationGate)
        {
            if (string.IsNullOrWhiteSpace(outboundAccelerationOfferNonce) ||
                !IsRuntimeUnlockActivationReason(outboundAccelerationOfferTrigger) ||
                runtimeUnlockOfferProofState is not { Retired: false } state ||
                outboundAccelerationOfferGeneration != state.Generation ||
                outboundAccelerationOfferPayerDecisionId != state.PayerDecisionId)
            {
                return;
            }

            var sanitizedReason = SanitizeLogToken(reason);
            runtimeUnlockQueueAcceptedObservedEscapeGeneration = state.Generation;
            runtimeUnlockQueueAcceptedObservedEscapePayerDecisionId = state.PayerDecisionId;
            runtimeUnlockQueueAcceptedObservedEscapeTick = Stopwatch.GetTimestamp();
            runtimeUnlockQueueAcceptedObservedEscapeReason = sanitizedReason;
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_control_queue_observed_escape_armed; session_id={SanitizeLogToken(state.SessionId)}; generation={state.Generation}; payer_decision_id={state.PayerDecisionId}; reason={sanitizedReason}");
        }
    }

    private void ClearRuntimeUnlockQueueAcceptedObservedEscapeLocked()
    {
        runtimeUnlockQueueAcceptedObservedEscapeGeneration = 0;
        runtimeUnlockQueueAcceptedObservedEscapePayerDecisionId = 0;
        runtimeUnlockQueueAcceptedObservedEscapeTick = 0;
        runtimeUnlockQueueAcceptedObservedEscapeReason = null;
    }

    private bool ShouldDeferPostTunaFallbackReceiveStallForRuntimeUnlockOffer(
        string? blockerReason,
        string? sessionId)
    {
        if (!IsReceiveStallActivationSendBlocker(blockerReason) ||
            !IsCurrentRuntimeUnlockActivationOffer())
        {
            return false;
        }

        if (TryGetUnresolvedFileTransferV6TransportEpochForCurrentSession(out var pendingEpoch) &&
            IsPostTunaFallbackRegularNknEpoch(pendingEpoch))
        {
            return true;
        }

        return HasActivePostTunaFallbackFileTransferRouteHint(sessionId);
    }

    private bool HasActivePostTunaFallbackFileTransferRouteHint(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var normalizedSessionId = sessionId.Trim();
        lock (gate)
        {
            foreach (var session in fileTransferDataSessions.Values)
            {
                if (session.IsDisposed ||
                    !string.Equals(session.SessionId, normalizedSessionId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (fileTransferRouteHints.TryGetValue(session.TransferId, out var routeHint) &&
                    routeHint.Route == FileTransferRoute.PostTunaFallbackV6)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool HasFreshPostTunaFallbackReceiverFrontierProofHint(
        string? sessionId,
        out FileTransferPostTunaFallbackRepairProofHint proofHint)
    {
        proofHint = default;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var normalizedSessionId = sessionId.Trim();
        var now = DateTimeOffset.UtcNow;
        lock (gate)
        {
            var staleTransferIds = fileTransferPostTunaFallbackRepairProofHints
                .Where(pair => now - pair.Value.ObservedUtc > FileTransferPostTunaFallbackRepairProofFreshness)
                .Select(static pair => pair.Key)
                .ToArray();
            foreach (var staleTransferId in staleTransferIds)
            {
                fileTransferPostTunaFallbackRepairProofHints.Remove(staleTransferId);
            }

            foreach (var session in fileTransferDataSessions.Values)
            {
                if (session.IsDisposed ||
                    !string.Equals(session.SessionId, normalizedSessionId, StringComparison.Ordinal) ||
                    !fileTransferRouteHints.TryGetValue(session.TransferId, out var routeHint) ||
                    routeHint.Route != FileTransferRoute.PostTunaFallbackV6 ||
                    !fileTransferPostTunaFallbackRepairProofHints.TryGetValue(session.TransferId, out var candidate) ||
                    !string.Equals(candidate.SessionId, normalizedSessionId, StringComparison.Ordinal) ||
                    now - candidate.ObservedUtc > FileTransferPostTunaFallbackRepairProofFreshness)
                {
                    continue;
                }

                proofHint = candidate;
                return true;
            }
        }

        return false;
    }

    private bool ShouldProtectPostTunaFallbackAvailabilityDuringRuntimeUnlockRecovery(
        string? sessionId,
        string recoveryReason,
        string trigger,
        out string protectReason)
    {
        protectReason = "none";
        if (!IsRuntimeUnlockPostTunaFallbackOfferRecoveryReason(recoveryReason) ||
            !HasFreshPostTunaFallbackReceiverFrontierProofHint(sessionId, out var proofHint))
        {
            return false;
        }

        var proofAgeMs = Math.Max(0, (long)(DateTimeOffset.UtcNow - proofHint.ObservedUtc).TotalMilliseconds);
        protectReason = $"fresh_{proofHint.ProofKind}_{proofHint.Direction}";
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=filetransfer_post_tuna_fallback_runtime_unlock_recovery_availability_protected; session_id={SanitizeLogToken(sessionId ?? "none")}; transfer_id={SanitizeLogToken(proofHint.TransferId)}; reason={SanitizeLogToken(recoveryReason)}; trigger={SanitizeLogToken(trigger)}; proof={SanitizeLogToken(proofHint.ProofKind)}; proof_direction={SanitizeLogToken(proofHint.Direction)}; proof_age_ms={proofAgeMs}; freshness_ms={(long)FileTransferPostTunaFallbackRepairProofFreshness.TotalMilliseconds}");
        return true;
    }

    private static bool IsRuntimeUnlockPostTunaFallbackOfferRecoveryReason(string? reason)
    {
        var normalized = SanitizeLogToken(reason);
        return normalized.StartsWith(
            "post_tuna_fallback_tuna_activation_offer",
            StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldPauseRuntimeUnlockOfferOnQueueAccepted()
    {
        return TryGetRuntimeUnlockOfferQueueAcceptedPressureReason(out _);
    }

    private bool ShouldPauseRegularNknV4FileTransferForRuntimeUnlock(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        if (HasActivePostTunaFallbackFileTransferRouteHint(sessionId))
        {
            return false;
        }

        if (TryGetUnresolvedFileTransferV6TransportEpochForCurrentSession(out var pendingEpoch) &&
            string.Equals(pendingEpoch.SessionId, sessionId, StringComparison.Ordinal) &&
            IsPostTunaFallbackRegularNknEpoch(pendingEpoch))
        {
            return false;
        }

        if (client is RealNknClientAdapter realClient &&
            realClient.HasActiveFileTransferRuntimeForActivationSend())
        {
            return true;
        }

        lock (gate)
        {
            foreach (var session in fileTransferDataSessions.Values)
            {
                if (session.IsDisposed ||
                    !string.Equals(session.SessionId, sessionId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (fileTransferRouteHints.TryGetValue(session.TransferId, out var routeHint) &&
                    routeHint.Route == FileTransferRoute.RegularNknV4Fast)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private string? GetRuntimeUnlockOfferQueueAcceptedObservedReason()
        => TryGetRuntimeUnlockQueueAcceptedObservedEscapeReason(out var reason)
            ? reason
            : null;

    private bool TryGetRuntimeUnlockOfferQueueAcceptedPressureReason(out string reason)
    {
        reason = string.Empty;
        if (!IsCurrentRuntimeUnlockActivationOffer())
        {
            return false;
        }

        if (RuntimeUnlockOfferQueueAcceptedPressureOverrideForTests?.Invoke(this) == true)
        {
            reason = "test_regular_v4_pressure";
            return true;
        }

        if (client is not RealNknClientAdapter realClient)
        {
            return TryGetRuntimeUnlockQueueAcceptedObservedEscapeReason(out reason);
        }

        if (realClient.TryGetFileTransferRegularV4ActivationSendPressure(out var pressureReason, out _))
        {
            reason = SanitizeLogToken(pressureReason ?? "regular_v4_pressure");
            return true;
        }

        if (TryGetRuntimeUnlockQueueAcceptedObservedEscapeReason(out reason))
        {
            return true;
        }

        return false;
    }

    private bool TryGetRuntimeUnlockQueueAcceptedObservedEscapeReason(out string reason)
    {
        reason = string.Empty;
        lock (accelerationGate)
        {
            if (string.IsNullOrWhiteSpace(outboundAccelerationOfferNonce) ||
                !IsRuntimeUnlockActivationReason(outboundAccelerationOfferTrigger) ||
                runtimeUnlockOfferProofState is not { Retired: false } state ||
                runtimeUnlockQueueAcceptedObservedEscapeGeneration != state.Generation ||
                runtimeUnlockQueueAcceptedObservedEscapePayerDecisionId != state.PayerDecisionId ||
                runtimeUnlockQueueAcceptedObservedEscapeTick <= 0 ||
                string.IsNullOrWhiteSpace(runtimeUnlockQueueAcceptedObservedEscapeReason))
            {
                return false;
            }

            if (Stopwatch.GetElapsedTime(runtimeUnlockQueueAcceptedObservedEscapeTick) >
                RuntimeUnlockQueueAcceptedObservedEscapeTtl)
            {
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_control_queue_observed_escape_expired; session_id={SanitizeLogToken(state.SessionId)}; generation={state.Generation}; payer_decision_id={state.PayerDecisionId}; reason={SanitizeLogToken(runtimeUnlockQueueAcceptedObservedEscapeReason)}");
                runtimeUnlockQueueAcceptedObservedEscapeGeneration = 0;
                runtimeUnlockQueueAcceptedObservedEscapePayerDecisionId = 0;
                runtimeUnlockQueueAcceptedObservedEscapeTick = 0;
                runtimeUnlockQueueAcceptedObservedEscapeReason = null;
                return false;
            }

            reason = SanitizeLogToken(runtimeUnlockQueueAcceptedObservedEscapeReason);
            return true;
        }
    }

    private bool IsCurrentRuntimeUnlockActivationOffer()
    {
        lock (accelerationGate)
        {
            return IsRuntimeUnlockActivationReason(outboundAccelerationOfferTrigger);
        }
    }

    private bool TryGetFileTransferTunaActivationBridgeRecoveryWaitSession(
        string purpose,
        string? activationSessionId,
        out string? sessionId)
    {
        if (TryGetActiveFileTransferTunaActivationPause(activationSessionId, out sessionId))
        {
            return true;
        }

        if (!IsTunaActivationOfferSendPurpose(purpose))
        {
            return false;
        }

        sessionId = string.IsNullOrWhiteSpace(activationSessionId)
            ? currentSessionSecurityState.SessionId?.Value
            : activationSessionId.Trim();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(activationSessionId))
        {
            return true;
        }

        lock (accelerationGate)
        {
            return !string.IsNullOrWhiteSpace(outboundAccelerationOfferNonce) &&
                   IsRuntimeUnlockActivationReason(outboundAccelerationOfferTrigger) &&
                   !IsAccelerationNegotiatedAndHealthyUnsafe(sessionId);
        }
    }

    private static bool IsTunaActivationOfferSendPurpose(string? purpose)
        => string.Equals(purpose, "offer", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(purpose, "offer_replay", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(purpose, "offer_answer", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(purpose, "offer_peer_response", StringComparison.OrdinalIgnoreCase);

    private static bool IsTunaActivationAnswerSendPurpose(string? purpose)
        => string.Equals(purpose, "answer", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(purpose, "answer_replay", StringComparison.OrdinalIgnoreCase);

    private bool IsRuntimeUnlockPeerVisibleActivationSendPurpose(string purpose, string? sessionId)
    {
        if (IsTunaActivationOfferSendPurpose(purpose))
        {
            return IsCurrentRuntimeUnlockActivationOffer();
        }

        return IsTunaActivationAnswerSendPurpose(purpose) &&
               IsPendingAccelerationAnswerAckForSession(sessionId);
    }

    private bool IsPendingRuntimeUnlockAccelerationAnswerAck(string? sessionId)
        => IsPendingAccelerationAnswerAckForSession(sessionId, requireRuntimeUnlock: true);

    private bool IsPendingAccelerationAnswerAckForSession(string? sessionId, bool requireRuntimeUnlock = false)
    {
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId)
            ? string.Empty
            : sessionId.Trim();
        lock (accelerationGate)
        {
            return !string.IsNullOrWhiteSpace(pendingAccelerationAnswerAckNonce) &&
                   (!requireRuntimeUnlock ||
                    IsRuntimeUnlockActivationReason(pendingAccelerationAnswerAckTrigger)) &&
                   (string.IsNullOrWhiteSpace(normalizedSessionId) ||
                    string.Equals(pendingAccelerationAnswerAckSessionId, normalizedSessionId, StringComparison.Ordinal));
        }
    }

    public bool TryGetActiveSessionRecoveryContract(
        string sessionId,
        out SessionRecoveryContractSnapshot snapshot)
    {
        snapshot = default!;
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? string.Empty : sessionId.Trim();
        if (string.IsNullOrWhiteSpace(normalizedSessionId))
        {
            return false;
        }

        var expiredAuthority = false;
        string? expiredSessionId = null;
        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            if (state is null ||
                !string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal))
            {
                return false;
            }

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (ShouldExpireRuntimeUnlockRetryAuthorityUnsafe(state, nowMs) &&
                state.ContractState is not (SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed))
            {
                state.RetryAuthorityPending = false;
                state.ObservedSendPending = false;
                state.AuthorityFailureReason = "runtime_unlock_retry_authority_expired";
                state.ContractState = SessionRecoveryContractState.Failed;
                expiredAuthority = true;
                expiredSessionId = state.SessionId;
            }

            snapshot = CreateSessionRecoveryContractSnapshotUnsafe(state);
        }

        if (expiredAuthority)
        {
            LogRuntimeUnlockRecoveryContract("session_recovery_contract_retry_authority_failed", expiredSessionId);
        }

        return true;
    }

    private SessionRecoveryContractSnapshot CreateSessionRecoveryContractSnapshotUnsafe(
        RuntimeUnlockRecoveryRetryState state)
        => new(
            state.SessionId,
            state.TransferId,
            state.ContractGeneration,
            state.CurrentOfferGeneration > 0 ? state.CurrentOfferGeneration : state.RetiredOfferGeneration,
            SessionRecoveryContractKind.RuntimeUnlockActivation,
            state.ContractState,
            state.RetryReason,
            state.RecoveryReason,
            DateTimeOffset.FromUnixTimeMilliseconds(state.CreatedUtcMs),
            DateTimeOffset.FromUnixTimeMilliseconds(state.RetryDeadlineUtcMs),
            DateTimeOffset.FromUnixTimeMilliseconds(state.LivenessDeferralDeadlineUtcMs),
            RecoveryPending: !state.Settled && state.ContractState == SessionRecoveryContractState.RecoveryPending,
            RecoverySettled: state.Settled,
            RetryRequired: IsSessionRecoveryContractRetryRequired(state),
            RetryDispatching: state.RetryDispatching,
            RetryDispatched: state.RetryDispatched,
            RetryObserved: state.RetryObserved,
            QueuedBehindActiveNegotiation: state.QueuedBehindActiveNegotiation,
            RetryAuthorityPending: state.RetryAuthorityPending,
            RetryAuthorityGranted: state.RetryAuthorityGranted,
            ObservedSendPending: state.ObservedSendPending,
            ObservedSendDeadlineUtc: state.ObservedSendDeadlineUtcMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(state.ObservedSendDeadlineUtcMs)
                : DateTimeOffset.MinValue,
            AuthorizedObservedLane: state.AuthorizedObservedLane,
            AuthorityFailureReason: state.AuthorityFailureReason,
            AuthorityAttempt: state.AuthorityAttempt,
            RuntimeUnlockTransactionGeneration: runtimeUnlockTransactionState.TransactionGeneration,
            RuntimeUnlockTransactionOfferGeneration: runtimeUnlockTransactionState.OfferGeneration,
            RuntimeUnlockTransactionState: runtimeUnlockTransactionState.State.ToString().ToLowerInvariant(),
            RuntimeUnlockPeerProofObserved: runtimeUnlockTransactionState.HasPeerVisibleProof,
            RuntimeUnlockRouteCommitPending: runtimeUnlockTransactionState.RouteCommitPending,
            RuntimeUnlockRouteCommitted: runtimeUnlockTransactionState.RouteCommitted,
            RuntimeUnlockTransactionFailureReason: runtimeUnlockTransactionState.FailureReason,
            RuntimeUnlockPathProbeId: runtimeUnlockTransactionState.PathProbeId,
            RuntimeUnlockPathProbeState: runtimeUnlockTransactionState.PathProbeState.ToString().ToLowerInvariant(),
            RuntimeUnlockPathProbeTransport: FormatFileTransferTransportKindForLog(runtimeUnlockTransactionState.PathProbeTransport),
            RuntimeUnlockPathProbeAckedUtcMs: runtimeUnlockTransactionState.PathProbeAckedUtcMs,
            RuntimeUnlockPathProbeFailureReason: runtimeUnlockTransactionState.PathProbeFailureReason,
            RuntimeUnlockTunaPathLeaseGeneration: runtimeUnlockTunaPathLeaseState.LeaseGeneration,
            RuntimeUnlockTunaPathLeaseState: runtimeUnlockTunaPathLeaseState.State.ToString().ToLowerInvariant(),
            RuntimeUnlockTunaPathLeaseListenerRunId: runtimeUnlockTunaPathLeaseState.ListenerRunId,
            RuntimeUnlockTunaPathLeaseCurrent: runtimeUnlockTunaPathLeaseState.IsCurrent,
            RuntimeUnlockTunaPathLeaseFailureReason: runtimeUnlockTunaPathLeaseState.FailureReason);

    void IRuntimeUnlockRouteCommitProofProvider.NotifyRuntimeUnlockPathProbeStarted(
        string sessionId,
        string transferId,
        long transportEpoch,
        string probeId,
        FileTransferTransportKind targetTransport,
        string reason)
    {
        NotifyRuntimeUnlockPathProbe(
            RuntimeUnlockTransactionEventKind.PathProbeStarted,
            sessionId,
            transferId,
            transportEpoch,
            probeId,
            targetTransport,
            acked: false,
            reason);
    }

    void IRuntimeUnlockRouteCommitProofProvider.NotifyRuntimeUnlockPathProbeResult(
        string sessionId,
        string transferId,
        long transportEpoch,
        string probeId,
        FileTransferTransportKind targetTransport,
        bool acked,
        string reason)
    {
        NotifyRuntimeUnlockPathProbe(
            acked
                ? RuntimeUnlockTransactionEventKind.PathProbeAcked
                : RuntimeUnlockTransactionEventKind.PathProbeFailed,
            sessionId,
            transferId,
            transportEpoch,
            probeId,
            targetTransport,
            acked,
            reason);
    }

    private void NotifyRuntimeUnlockPathProbe(
        RuntimeUnlockTransactionEventKind kind,
        string sessionId,
        string transferId,
        long transportEpoch,
        string probeId,
        FileTransferTransportKind targetTransport,
        bool acked,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(sessionId) ||
            string.IsNullOrWhiteSpace(probeId))
        {
            return;
        }

        RuntimeUnlockTransactionDecision? decision;
        lock (accelerationGate)
        {
            var current = runtimeUnlockTransactionState;
            if (current.IsTerminal ||
                !string.Equals(current.SessionId, sessionId.Trim(), StringComparison.Ordinal) ||
                (!string.IsNullOrWhiteSpace(current.TransferId) &&
                 !string.IsNullOrWhiteSpace(transferId) &&
                 !string.Equals(current.TransferId, transferId.Trim(), StringComparison.Ordinal)))
            {
                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    "event=runtime_unlock_probe_stale_ignored; " +
                    $"session_id={SanitizeLogToken(sessionId)}; transfer_id={SanitizeLogToken(transferId)}; " +
                    $"transaction_generation={current.TransactionGeneration}; offer_generation={current.OfferGeneration}; " +
                    $"probe_id={SanitizeLogToken(probeId)}; target_transport={FormatFileTransferTransportKindForLog(targetTransport)}; " +
                    $"transport_epoch={transportEpoch}; state={current.State.ToString().ToLowerInvariant()}; reason={SanitizeLogToken(reason)}");
                return;
            }

            decision = ApplyRuntimeUnlockTransactionUnsafe(
                kind,
                sessionId.Trim(),
                current.OfferGeneration,
                reason,
                transactionGeneration: current.TransactionGeneration,
                pathProbeId: probeId.Trim(),
                pathProbeTransport: targetTransport);
        }

        LogRuntimeUnlockTransactionDecision(kind, decision);
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event={GetRuntimeUnlockProbeEventName(kind)}; session_id={SanitizeLogToken(sessionId)}; transfer_id={SanitizeLogToken(transferId)}; transaction_generation={decision.State.TransactionGeneration}; offer_generation={decision.State.OfferGeneration}; probe_id={SanitizeLogToken(probeId)}; target_transport={FormatFileTransferTransportKindForLog(targetTransport)}; transport_epoch={transportEpoch}; acked={(acked ? 1 : 0)}; state={decision.State.PathProbeState.ToString().ToLowerInvariant()}; reason={SanitizeLogToken(reason)}");
    }

    bool IRuntimeUnlockRouteCommitProofProvider.TryGetRuntimeUnlockRouteCommitProof(
        string sessionId,
        string transferId,
        out RuntimeUnlockRouteCommitSnapshot snapshot)
    {
        snapshot = default!;
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? string.Empty : sessionId.Trim();
        var normalizedTransferId = string.IsNullOrWhiteSpace(transferId) ? string.Empty : transferId.Trim();
        if (string.IsNullOrWhiteSpace(normalizedSessionId))
        {
            return false;
        }

        lock (accelerationGate)
        {
            var state = runtimeUnlockTransactionState;
            if (state.IsTerminal ||
                !state.HasPeerVisibleProof ||
                !string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal) ||
                (!string.IsNullOrWhiteSpace(state.TransferId) &&
                 !string.IsNullOrWhiteSpace(normalizedTransferId) &&
                 !string.Equals(state.TransferId, normalizedTransferId, StringComparison.Ordinal)))
            {
                return false;
            }

            var lease = runtimeUnlockTunaPathLeaseState;
            var leaseRequired = RuntimeUnlockTunaPathLease.IsApplicableToOffer(
                lease,
                state.SessionId,
                state.TransactionGeneration,
                state.OfferGeneration);
            snapshot = new RuntimeUnlockRouteCommitSnapshot(
                state.SessionId,
                state.TransferId,
                state.TransactionGeneration,
                state.OfferGeneration,
                state.HasPeerVisibleProof,
                state.PeerReceived,
                state.AnswerReceived,
                FileTransferRoute.FileTunaV4,
                FileTransferProtocol.ProtocolVersionV4,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                state.State.ToString(),
                "runtime_unlock_transaction_peer_proof",
                PathProbeId: state.PathProbeId,
                PathProbeState: state.PathProbeState.ToString(),
                PathProbeTransport: state.PathProbeTransport,
                PathProbeAckedUtcMs: state.PathProbeAckedUtcMs,
                PathProbeFailureReason: state.PathProbeFailureReason,
                TunaPathLeaseRequired: leaseRequired,
                TunaPathLeaseGeneration: leaseRequired ? lease.LeaseGeneration : 0,
                TunaPathLeaseState: leaseRequired ? lease.State.ToString() : "None",
                TunaPathLeaseListenerRunId: leaseRequired ? lease.ListenerRunId : null,
                TunaPathLeaseCurrent: leaseRequired && lease.IsCurrent,
                TunaPathLeaseFailureReason: leaseRequired ? lease.FailureReason : null);
            return true;
        }
    }

    void IRuntimeUnlockRouteCommitProofProvider.NotifyRuntimeUnlockRouteCommitResult(
        string sessionId,
        string transferId,
        long transactionGeneration,
        long offerGeneration,
        bool accepted,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(sessionId) ||
            transactionGeneration <= 0 ||
            offerGeneration <= 0)
        {
            return;
        }

        RuntimeUnlockTransactionDecision? decision;
        var normalizedReason = string.IsNullOrWhiteSpace(reason)
            ? (accepted ? "route_commit_accepted" : "route_commit_rejected")
            : reason.Trim();
        lock (accelerationGate)
        {
            var current = runtimeUnlockTransactionState;
            if (current.IsTerminal ||
                current.TransactionGeneration != transactionGeneration ||
                current.OfferGeneration != offerGeneration ||
                !string.Equals(current.SessionId, sessionId.Trim(), StringComparison.Ordinal))
            {
                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    "event=runtime_unlock_transaction_commit_result_stale; " +
                    $"session_id={SanitizeLogToken(sessionId)}; transfer_id={SanitizeLogToken(transferId)}; " +
                    $"transaction_generation={transactionGeneration}; offer_generation={offerGeneration}; " +
                    $"current_transaction_generation={current.TransactionGeneration}; current_offer_generation={current.OfferGeneration}; " +
                    $"current_state={current.State.ToString().ToLowerInvariant()}; accepted={(accepted ? 1 : 0)}; " +
                    $"reason={SanitizeLogToken(normalizedReason)}");
                return;
            }

            decision = ApplyRuntimeUnlockTransactionUnsafe(
                accepted
                    ? RuntimeUnlockTransactionEventKind.RouteCommitted
                    : RuntimeUnlockTransactionEventKind.Failed,
                sessionId.Trim(),
                offerGeneration,
                accepted ? normalizedReason : $"route_commit_rejected_{normalizedReason}",
                transactionGeneration: transactionGeneration);
            if (accepted)
            {
                ClearRuntimeUnlockBrokenStateRetryBudgetUnsafe(sessionId);
            }
        }

        if (accepted)
        {
            TrackFileTransferRouteHint(
                transferId,
                FileTransferRouteResolver.FileTunaV4Token,
                FileTransferProtocol.ProtocolVersionV4,
                "runtime_unlock_route_commit_accepted");
        }

        LogRuntimeUnlockTransactionDecision(
            accepted
                ? RuntimeUnlockTransactionEventKind.RouteCommitted
                : RuntimeUnlockTransactionEventKind.Failed,
            decision);
    }

    private RuntimeUnlockTransactionDecision StartRuntimeUnlockTransactionForOfferUnsafe(
        string sessionId,
        long offerGeneration,
        string reason)
    {
        var current = runtimeUnlockTransactionState;
        var transactionGeneration =
            current is
            {
                IsTerminal: false,
            } &&
            string.Equals(current.SessionId, sessionId.Trim(), StringComparison.Ordinal)
                ? current.TransactionGeneration
                : ++runtimeUnlockTransactionNextGeneration;
        if (transactionGeneration <= 0)
        {
            transactionGeneration = ++runtimeUnlockTransactionNextGeneration;
        }

        return ApplyRuntimeUnlockTransactionUnsafe(
            RuntimeUnlockTransactionEventKind.OfferGenerationCreated,
            sessionId,
            offerGeneration,
            reason,
            transactionGeneration: transactionGeneration);
    }

    private void ApplyRuntimeUnlockTransaction(
        RuntimeUnlockTransactionEventKind kind,
        string? sessionId,
        long offerGeneration,
        string reason,
        string? observedLane = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        RuntimeUnlockTransactionDecision? decision;
        lock (accelerationGate)
        {
            decision = ApplyRuntimeUnlockTransactionUnsafe(
                kind,
                sessionId.Trim(),
                offerGeneration,
                reason,
                observedLane: observedLane);
        }

        LogRuntimeUnlockTransactionDecision(kind, decision);
    }

    private RuntimeUnlockTransactionDecision ApplyRuntimeUnlockTransactionUnsafe(
        RuntimeUnlockTransactionEventKind kind,
        string sessionId,
        long offerGeneration,
        string reason,
        long transactionGeneration = 0,
        string? observedLane = null,
        string? pathProbeId = null,
        FileTransferTransportKind pathProbeTransport = FileTransferTransportKind.Unknown)
    {
        var effectiveTransactionGeneration = transactionGeneration > 0
            ? transactionGeneration
            : runtimeUnlockTransactionState.TransactionGeneration;
        var decision = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                kind,
                sessionId,
                TransferId: null,
                effectiveTransactionGeneration,
                offerGeneration,
                SanitizeLogToken(reason),
                UtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ObservedLane: observedLane,
                PathProbeId: pathProbeId,
                PathProbeTransport: pathProbeTransport),
            runtimeUnlockTransactionState);
        var stateWithLease = AttachRuntimeUnlockTunaPathLeaseUnsafe(decision.State);
        runtimeUnlockTransactionState = stateWithLease;
        return decision with { State = stateWithLease };
    }

    private RuntimeUnlockTransactionSnapshot AttachRuntimeUnlockTunaPathLeaseUnsafe(
        RuntimeUnlockTransactionSnapshot state)
    {
        var lease = runtimeUnlockTunaPathLeaseState;
        if (!RuntimeUnlockTunaPathLease.IsApplicableToOffer(
                lease,
                state.SessionId,
                state.TransactionGeneration,
                state.OfferGeneration))
        {
            return state with
            {
                TunaPathLeaseGeneration = 0,
                TunaPathLeaseState = RuntimeUnlockTunaPathLeaseState.None,
                TunaPathLeaseListenerRunId = null,
                TunaPathLeaseCurrent = false,
                TunaPathLeaseFailureReason = null,
            };
        }

        return state with
        {
            TunaPathLeaseGeneration = lease.LeaseGeneration,
            TunaPathLeaseState = lease.State,
            TunaPathLeaseListenerRunId = lease.ListenerRunId,
            TunaPathLeaseCurrent = lease.IsCurrent,
            TunaPathLeaseFailureReason = lease.FailureReason,
        };
    }

    private void LogRuntimeUnlockTransactionDecision(
        RuntimeUnlockTransactionEventKind kind,
        RuntimeUnlockTransactionDecision? decision)
    {
        if (decision is null)
        {
            return;
        }

        var state = decision.State;
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=runtime_unlock_transaction_{GetRuntimeUnlockTransactionEventName(kind)}; session_id={SanitizeLogToken(state.SessionId)}; transaction_generation={state.TransactionGeneration}; offer_generation={state.OfferGeneration}; state={state.State.ToString().ToLowerInvariant()}; peer_visible_proof={(state.HasPeerVisibleProof ? 1 : 0)}; peer_received={(state.PeerReceived ? 1 : 0)}; answer_received={(state.AnswerReceived ? 1 : 0)}; path_probe_id={SanitizeLogToken(state.PathProbeId ?? "(none)")}; path_probe_state={state.PathProbeState.ToString().ToLowerInvariant()}; path_probe_transport={FormatFileTransferTransportKindForLog(state.PathProbeTransport)}; path_probe_acked_utc_ms={state.PathProbeAckedUtcMs}; path_probe_failure_reason={SanitizeLogToken(state.PathProbeFailureReason ?? "(none)")}; route_commit_pending={(state.RouteCommitPending ? 1 : 0)}; route_committed={(state.RouteCommitted ? 1 : 0)}; failure_reason={SanitizeLogToken(state.FailureReason ?? "(none)")}; retired_reason={SanitizeLogToken(state.RetiredReason ?? "(none)")}; tuna_path_lease_generation={state.TunaPathLeaseGeneration}; tuna_path_lease_state={state.TunaPathLeaseState.ToString().ToLowerInvariant()}; tuna_path_lease_current={(state.TunaPathLeaseCurrent ? 1 : 0)}; tuna_path_lease_listener_run_id={SanitizeLogToken(state.TunaPathLeaseListenerRunId ?? "(none)")}; tuna_path_lease_failure_reason={SanitizeLogToken(state.TunaPathLeaseFailureReason ?? "(none)")}; reason={SanitizeLogToken(decision.Reason)}");
    }

    private static string GetRuntimeUnlockTransactionEventName(RuntimeUnlockTransactionEventKind kind)
        => kind switch
        {
            RuntimeUnlockTransactionEventKind.ListenerReady => "listener_ready",
            RuntimeUnlockTransactionEventKind.ListenerUnavailable => "listener_unavailable",
            RuntimeUnlockTransactionEventKind.OfferGenerationCreated => "offer_generation_created",
            RuntimeUnlockTransactionEventKind.ObservedSend => "observed_send",
            RuntimeUnlockTransactionEventKind.PeerReceived => "peer_received",
            RuntimeUnlockTransactionEventKind.AnswerReceived => "answer_received",
            RuntimeUnlockTransactionEventKind.PathProbeStarted => "path_probe_started",
            RuntimeUnlockTransactionEventKind.PathProbeAcked => "path_probe_acked",
            RuntimeUnlockTransactionEventKind.PathProbeFailed => "path_probe_failed",
            RuntimeUnlockTransactionEventKind.Timeout => "timeout",
            RuntimeUnlockTransactionEventKind.BridgeRecoveryStarted => "bridge_recovery_started",
            RuntimeUnlockTransactionEventKind.BridgeRecoverySettled => "bridge_recovery_settled",
            RuntimeUnlockTransactionEventKind.BridgeRecoveryExhausted => "bridge_recovery_exhausted",
            RuntimeUnlockTransactionEventKind.LivenessDeadline => "liveness_deadline",
            RuntimeUnlockTransactionEventKind.RouteCommitted => "route_committed",
            RuntimeUnlockTransactionEventKind.Terminalized => "terminalized",
            RuntimeUnlockTransactionEventKind.Failed => "failed",
            RuntimeUnlockTransactionEventKind.Retired => "retired",
            _ => kind.ToString().ToLowerInvariant(),
        };

    private static string GetRuntimeUnlockProbeEventName(RuntimeUnlockTransactionEventKind kind)
        => kind switch
        {
            RuntimeUnlockTransactionEventKind.PathProbeStarted => "runtime_unlock_probe_started",
            RuntimeUnlockTransactionEventKind.PathProbeAcked => "runtime_unlock_probe_acked",
            RuntimeUnlockTransactionEventKind.PathProbeFailed => "runtime_unlock_probe_failed",
            _ => "runtime_unlock_probe_event",
        };

    private RuntimeUnlockTunaPathLeaseSnapshot StartRuntimeUnlockTunaPathLeaseUnsafe(
        string sessionId,
        long payerDecisionId,
        string reason)
    {
        var leaseGeneration = ++runtimeUnlockTunaPathLeaseNextGeneration;
        var listenerRunId = $"listener-start-{leaseGeneration.ToString(CultureInfo.InvariantCulture)}";
        var lease = RuntimeUnlockTunaPathLease.Start(
            sessionId,
            transferId: null,
            leaseGeneration,
            listenerRunId,
            payerDecisionId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        runtimeUnlockTunaPathLeaseState = lease;
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_path_lease_started; session_id={SanitizeLogToken(lease.SessionId)}; transfer_id=(none); transaction_generation={lease.TransactionGeneration}; offer_generation={lease.OfferGeneration}; lease_generation={lease.LeaseGeneration}; listener_run_id={SanitizeLogToken(lease.ListenerRunId ?? "(none)")}; payer_decision_id={lease.PayerDecisionId}; state={lease.State.ToString().ToLowerInvariant()}; reason={SanitizeLogToken(reason)}");
        return lease;
    }

    private RuntimeUnlockTunaPathLeaseSnapshot BindRuntimeUnlockTunaPathLeaseToOfferUnsafe(
        RuntimeUnlockTunaPathLeaseSnapshot lease,
        long transactionGeneration,
        long offerGeneration,
        long payerDecisionId,
        string reason)
    {
        if (lease.LeaseGeneration <= 0 ||
            runtimeUnlockTunaPathLeaseState.LeaseGeneration != lease.LeaseGeneration)
        {
            return runtimeUnlockTunaPathLeaseState;
        }

        var bound = RuntimeUnlockTunaPathLease.BindOffer(
            runtimeUnlockTunaPathLeaseState,
            transactionGeneration,
            offerGeneration,
            payerDecisionId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        runtimeUnlockTunaPathLeaseState = bound;
        runtimeUnlockTransactionState = AttachRuntimeUnlockTunaPathLeaseUnsafe(runtimeUnlockTransactionState);
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_path_lease_listener_starting; session_id={SanitizeLogToken(bound.SessionId)}; transfer_id=(none); transaction_generation={bound.TransactionGeneration}; offer_generation={bound.OfferGeneration}; lease_generation={bound.LeaseGeneration}; listener_run_id={SanitizeLogToken(bound.ListenerRunId ?? "(none)")}; payer_decision_id={bound.PayerDecisionId}; state={bound.State.ToString().ToLowerInvariant()}; reason={SanitizeLogToken(reason)}");
        return bound;
    }

    private void MarkRuntimeUnlockTunaPathLeaseListenerReady(
        RuntimeUnlockTunaPathLeaseSnapshot lease,
        string sessionId,
        string reason)
    {
        RuntimeUnlockTunaPathLeaseSnapshot? loggedLease = null;
        RuntimeUnlockTunaPathLeaseSnapshot? staleLease = null;
        RuntimeUnlockTransactionDecision? transactionDecision = null;
        lock (accelerationGate)
        {
            if (RuntimeUnlockTunaPathLease.TryMarkListenerReady(
                    runtimeUnlockTunaPathLeaseState,
                    sessionId,
                    lease.LeaseGeneration,
                    lease.ListenerRunId,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    out var updated))
            {
                runtimeUnlockTunaPathLeaseState = updated;
                if (!runtimeUnlockTransactionState.IsTerminal &&
                    string.Equals(runtimeUnlockTransactionState.SessionId, updated.SessionId, StringComparison.Ordinal))
                {
                    transactionDecision = ApplyRuntimeUnlockTransactionUnsafe(
                        RuntimeUnlockTransactionEventKind.ListenerReady,
                        updated.SessionId,
                        runtimeUnlockTransactionState.OfferGeneration,
                        reason);
                }

                runtimeUnlockTransactionState = AttachRuntimeUnlockTunaPathLeaseUnsafe(runtimeUnlockTransactionState);
                loggedLease = updated;
            }
            else if (lease.LeaseGeneration > 0)
            {
                staleLease = lease;
            }
        }

        if (transactionDecision is not null)
        {
            LogRuntimeUnlockTransactionDecision(RuntimeUnlockTransactionEventKind.ListenerReady, transactionDecision);
        }

        if (loggedLease is not null)
        {
            LogRuntimeUnlockTunaPathLease("tuna_path_lease_listener_ready", loggedLease.Value, reason);
        }
        else if (staleLease is not null)
        {
            LogRuntimeUnlockTunaPathLease("tuna_path_lease_stale_listener_event_ignored", staleLease.Value, reason);
        }
    }

    private bool FailRuntimeUnlockTunaPathLeaseIfCurrent(
        string? sessionId,
        long offerGeneration,
        string reason,
        bool failTransaction)
    {
        RuntimeUnlockTunaPathLeaseSnapshot? failedLease = null;
        RuntimeUnlockTransactionDecision? transactionDecision = null;
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? string.Empty : sessionId.Trim();
        lock (accelerationGate)
        {
            var lease = runtimeUnlockTunaPathLeaseState;
            if (lease.LeaseGeneration <= 0 ||
                lease.State is RuntimeUnlockTunaPathLeaseState.Failed or RuntimeUnlockTunaPathLeaseState.Retired ||
                (!string.IsNullOrWhiteSpace(normalizedSessionId) &&
                 !string.Equals(lease.SessionId, normalizedSessionId, StringComparison.Ordinal)) ||
                (offerGeneration > 0 && lease.OfferGeneration > 0 && lease.OfferGeneration != offerGeneration))
            {
                return false;
            }

            var failed = RuntimeUnlockTunaPathLease.Fail(
                lease,
                reason,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            runtimeUnlockTunaPathLeaseState = failed;
            runtimeUnlockTransactionState = AttachRuntimeUnlockTunaPathLeaseUnsafe(runtimeUnlockTransactionState);
            if (failTransaction &&
                !runtimeUnlockTransactionState.IsTerminal &&
                string.Equals(runtimeUnlockTransactionState.SessionId, failed.SessionId, StringComparison.Ordinal))
            {
                transactionDecision = ApplyRuntimeUnlockTransactionUnsafe(
                    RuntimeUnlockTransactionEventKind.Failed,
                    failed.SessionId,
                    runtimeUnlockTransactionState.OfferGeneration,
                    reason);
            }

            failedLease = failed;
        }

        if (transactionDecision is not null)
        {
            LogRuntimeUnlockTransactionDecision(RuntimeUnlockTransactionEventKind.Failed, transactionDecision);
        }

        if (failedLease is not null)
        {
            LogRuntimeUnlockTunaPathLease("tuna_path_lease_failed", failedLease.Value, reason);
            if (reason.Contains("sidecar", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("listener", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("tuna_path_lease_unavailable", StringComparison.OrdinalIgnoreCase))
            {
                LogRuntimeUnlockTunaPathLease("tuna_path_lease_sidecar_unavailable", failedLease.Value, reason);
            }
        }

        return failedLease is not null;
    }

    private bool IsRuntimeUnlockTunaPathLeaseUnavailableForAnswer(
        string sessionId,
        long offerGeneration,
        out string reason)
    {
        reason = "none";
        lock (accelerationGate)
        {
            var lease = runtimeUnlockTunaPathLeaseState;
            if (!RuntimeUnlockTunaPathLease.IsApplicableToOffer(
                    lease,
                    sessionId,
                    runtimeUnlockTransactionState.TransactionGeneration,
                    offerGeneration))
            {
                return false;
            }

            if (RuntimeUnlockTunaPathLease.CanSatisfyRouteCommit(lease))
            {
                return false;
            }

            reason = lease.State == RuntimeUnlockTunaPathLeaseState.Failed
                ? lease.FailureReason ?? "tuna_path_lease_failed"
                : "tuna_path_lease_unavailable";
            return true;
        }
    }

    private bool HasFailedRuntimeUnlockTunaPathLeaseForCurrentSession(string? sessionId)
    {
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? currentSessionSecurityState.SessionId?.Value ?? string.Empty : sessionId.Trim();
        lock (accelerationGate)
        {
            return runtimeUnlockTunaPathLeaseState.LeaseGeneration > 0 &&
                   runtimeUnlockTunaPathLeaseState.State == RuntimeUnlockTunaPathLeaseState.Failed &&
                   (string.IsNullOrWhiteSpace(normalizedSessionId) ||
                    string.Equals(runtimeUnlockTunaPathLeaseState.SessionId, normalizedSessionId, StringComparison.Ordinal));
        }
    }

    private void LogRuntimeUnlockTunaPathLease(
        string eventName,
        RuntimeUnlockTunaPathLeaseSnapshot lease,
        string reason)
        => LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event={SanitizeLogToken(eventName)}; session_id={SanitizeLogToken(lease.SessionId)}; transfer_id={SanitizeLogToken(lease.TransferId ?? "(none)")}; transaction_generation={lease.TransactionGeneration}; offer_generation={lease.OfferGeneration}; lease_generation={lease.LeaseGeneration}; listener_run_id={SanitizeLogToken(lease.ListenerRunId ?? "(none)")}; payer_decision_id={lease.PayerDecisionId}; state={lease.State.ToString().ToLowerInvariant()}; current={(lease.IsCurrent ? 1 : 0)}; failure_reason={SanitizeLogToken(lease.FailureReason ?? "(none)")}; retired_reason={SanitizeLogToken(lease.RetiredReason ?? "(none)")}; reason={SanitizeLogToken(reason)}");

    private static bool IsSessionRecoveryContractRetryRequired(RuntimeUnlockRecoveryRetryState state)
        => !state.RetryObserved &&
           state.ContractState is SessionRecoveryContractState.RecoveryPending or
               SessionRecoveryContractState.RecoverySettled or
               SessionRecoveryContractState.RetryQueued or
               SessionRecoveryContractState.RetryDispatching;

    private void LogRuntimeUnlockRecoveryContract(string eventName, string? sessionId)
    {
        RuntimeUnlockRecoveryRetryState? stateSnapshot;
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? string.Empty : sessionId.Trim();
        lock (accelerationGate)
        {
            stateSnapshot = runtimeUnlockRecoveryRetryState;
            if (stateSnapshot is null ||
                (!string.IsNullOrWhiteSpace(normalizedSessionId) &&
                 !string.Equals(stateSnapshot.SessionId, normalizedSessionId, StringComparison.Ordinal)))
            {
                return;
            }
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event={SanitizeLogToken(eventName)}; session_id={SanitizeLogToken(stateSnapshot.SessionId)}; transfer_id={SanitizeLogToken(stateSnapshot.TransferId ?? "(none)")}; contract_generation={stateSnapshot.ContractGeneration}; offer_generation={(stateSnapshot.CurrentOfferGeneration > 0 ? stateSnapshot.CurrentOfferGeneration : stateSnapshot.RetiredOfferGeneration)}; retired_offer_generation={stateSnapshot.RetiredOfferGeneration}; kind=runtime_unlock_activation; state={SanitizeLogToken(stateSnapshot.ContractState.ToString().ToLowerInvariant())}; retry_reason={SanitizeLogToken(stateSnapshot.RetryReason)}; recovery_reason={SanitizeLogToken(stateSnapshot.RecoveryReason)}; recovery_pending={(!stateSnapshot.Settled ? 1 : 0)}; recovery_settled={(stateSnapshot.Settled ? 1 : 0)}; retry_required={(IsSessionRecoveryContractRetryRequired(stateSnapshot) ? 1 : 0)}; retry_dispatching={(stateSnapshot.RetryDispatching ? 1 : 0)}; retry_dispatched={(stateSnapshot.RetryDispatched ? 1 : 0)}; retry_observed={(stateSnapshot.RetryObserved ? 1 : 0)}; queued_behind_active_negotiation={(stateSnapshot.QueuedBehindActiveNegotiation ? 1 : 0)}; retry_authority_pending={(stateSnapshot.RetryAuthorityPending ? 1 : 0)}; retry_authority_granted={(stateSnapshot.RetryAuthorityGranted ? 1 : 0)}; observed_send_pending={(stateSnapshot.ObservedSendPending ? 1 : 0)}; authority_attempt={stateSnapshot.AuthorityAttempt}; authorized_observed_lane={SanitizeLogToken(stateSnapshot.AuthorizedObservedLane ?? "(none)")}; authority_failure_reason={SanitizeLogToken(stateSnapshot.AuthorityFailureReason ?? "(none)")}; requires_local_listener_retry={(stateSnapshot.RequiresLocalListenerRetry ? 1 : 0)}; listener_rearm_completed={(stateSnapshot.ListenerRearmCompleted ? 1 : 0)}; cutthrough_pending={(stateSnapshot.CutThroughPending ? 1 : 0)}; cutthrough_active={(stateSnapshot.CutThroughActive ? 1 : 0)}; cutthrough_offer_sent={(stateSnapshot.CutThroughOfferSent ? 1 : 0)}; cutthrough_peer_received={(stateSnapshot.CutThroughPeerReceived ? 1 : 0)}; cutthrough_completed={(stateSnapshot.CutThroughCompleted ? 1 : 0)}; cutthrough_attempt={stateSnapshot.CutThroughAttempt}; broken_state_fresh_retry_attempt={stateSnapshot.BrokenStateFreshRetryAttempt}; broken_state_fresh_retry_limit={stateSnapshot.BrokenStateFreshRetryLimit}; broken_state_finalized={(stateSnapshot.BrokenStateFinalized ? 1 : 0)}; broken_state_failure_reason={SanitizeLogToken(stateSnapshot.BrokenStateFailureReason ?? "(none)")}; observed_send_deadline_utc_ms={stateSnapshot.ObservedSendDeadlineUtcMs}; retry_deadline_utc_ms={stateSnapshot.RetryDeadlineUtcMs}; liveness_deferral_deadline_utc_ms={stateSnapshot.LivenessDeferralDeadlineUtcMs}");
    }

    private string? TryGetFirstActiveFileTransferIdForSession(string sessionId)
    {
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? string.Empty : sessionId.Trim();
        if (string.IsNullOrWhiteSpace(normalizedSessionId))
        {
            return null;
        }

        lock (gate)
        {
            foreach (var session in fileTransferDataSessions.Values)
            {
                if (!session.IsDisposed &&
                    string.Equals(session.SessionId, normalizedSessionId, StringComparison.Ordinal))
                {
                    return session.TransferId;
                }
            }
        }

        return null;
    }

    private bool IsActiveRegularV4FileTransferRouteHint(string? sessionId, string? transferId)
    {
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? string.Empty : sessionId.Trim();
        var normalizedTransferId = string.IsNullOrWhiteSpace(transferId) ? string.Empty : transferId.Trim();
        if (string.IsNullOrWhiteSpace(normalizedSessionId) ||
            string.IsNullOrWhiteSpace(normalizedTransferId))
        {
            return false;
        }

        lock (gate)
        {
            if (!fileTransferDataSessions.TryGetValue(normalizedTransferId, out var session) ||
                session.IsDisposed ||
                !string.Equals(session.SessionId, normalizedSessionId, StringComparison.Ordinal))
            {
                return false;
            }

            return !fileTransferRouteHints.TryGetValue(normalizedTransferId, out var routeHint) ||
                   routeHint.Route == FileTransferRoute.RegularNknV4Fast;
        }
    }

    private bool TryRequestFileTransferTunaActivationOfferSendRecovery(
        string purpose,
        string? activationSessionId,
        string trigger,
        out string? recoveryReason,
        out string? recoverySessionId)
    {
        recoveryReason = null;
        recoverySessionId = null;
        if (!IsTunaActivationOfferSendPurpose(purpose))
        {
            return false;
        }

        var hasActivationPause = TryGetActiveFileTransferTunaActivationPause(activationSessionId, out var sessionId);
        var postTunaFallbackOfferRecovery = false;
        var regularV4RuntimeUnlockOfferRecovery = false;
        var activeFileTransferRuntimeUnlockOfferRecovery = false;
        var answerTimeoutRuntimeUnlockOfferRecovery = false;
        var peerResponseTimeoutRuntimeUnlockOfferRecovery = false;
        if (!hasActivationPause)
        {
            postTunaFallbackOfferRecovery =
                TryGetPostTunaFallbackRuntimeUnlockOfferRecoverySession(activationSessionId, out sessionId);
        }

        if (!hasActivationPause &&
            !postTunaFallbackOfferRecovery)
        {
            regularV4RuntimeUnlockOfferRecovery =
                TryGetRegularV4RuntimeUnlockOfferRecoverySession(activationSessionId, out sessionId);
        }

        if (!hasActivationPause &&
            !postTunaFallbackOfferRecovery &&
            !regularV4RuntimeUnlockOfferRecovery)
        {
            activeFileTransferRuntimeUnlockOfferRecovery =
                TryGetRuntimeUnlockOfferActiveFileTransferRecoverySession(activationSessionId, out sessionId);
        }

        if (!hasActivationPause &&
            !postTunaFallbackOfferRecovery &&
            !regularV4RuntimeUnlockOfferRecovery &&
            !activeFileTransferRuntimeUnlockOfferRecovery &&
            string.Equals(purpose, "offer_answer", StringComparison.OrdinalIgnoreCase))
        {
            sessionId = string.IsNullOrWhiteSpace(activationSessionId)
                ? currentSessionSecurityState.SessionId?.Value
                : activationSessionId.Trim();
            answerTimeoutRuntimeUnlockOfferRecovery =
                !string.IsNullOrWhiteSpace(sessionId) &&
                (HasActiveRegularV4FileTransferRouteHint(sessionId) ||
                 HasActivePostTunaFallbackFileTransferRouteHint(sessionId) ||
                 HasActiveFileTransferDataSessionForSession(sessionId));
        }

        if (!hasActivationPause &&
            !postTunaFallbackOfferRecovery &&
            !regularV4RuntimeUnlockOfferRecovery &&
            !activeFileTransferRuntimeUnlockOfferRecovery &&
            !answerTimeoutRuntimeUnlockOfferRecovery &&
            string.Equals(purpose, "offer_peer_response", StringComparison.OrdinalIgnoreCase))
        {
            sessionId = string.IsNullOrWhiteSpace(activationSessionId)
                ? currentSessionSecurityState.SessionId?.Value
                : activationSessionId.Trim();
            peerResponseTimeoutRuntimeUnlockOfferRecovery =
                !string.IsNullOrWhiteSpace(sessionId) &&
                (HasActiveRegularV4FileTransferRouteHint(sessionId) ||
                 HasActivePostTunaFallbackFileTransferRouteHint(sessionId) ||
                 HasActiveFileTransferDataSessionForSession(sessionId));
        }

        if (!hasActivationPause &&
            !postTunaFallbackOfferRecovery &&
            !regularV4RuntimeUnlockOfferRecovery &&
            !activeFileTransferRuntimeUnlockOfferRecovery &&
            !answerTimeoutRuntimeUnlockOfferRecovery &&
            !peerResponseTimeoutRuntimeUnlockOfferRecovery)
        {
            return false;
        }

        var normalizedPurpose = SanitizeLogToken(purpose);
        var reason =
            string.Equals(purpose, "offer_replay", StringComparison.OrdinalIgnoreCase)
                ? "tuna_activation_offer_replay_send_timeout"
                : string.Equals(purpose, "offer_answer", StringComparison.OrdinalIgnoreCase)
                    ? "tuna_activation_offer_answer_timeout"
                    : string.Equals(purpose, "offer_peer_response", StringComparison.OrdinalIgnoreCase)
                        ? "tuna_activation_offer_peer_response_timeout"
                        : "tuna_activation_offer_send_timeout";
        var bridgeRecoveryReason = reason;
        if (postTunaFallbackOfferRecovery)
        {
            bridgeRecoveryReason = $"post_tuna_fallback_{reason}";
        }
        else if (regularV4RuntimeUnlockOfferRecovery &&
                 TryGetRuntimeUnlockRetryAuthorityForCurrentOffer(out _))
        {
            bridgeRecoveryReason = "runtime_unlock_retry_authority_offer_blocked";
            MarkRuntimeUnlockRecoveryContractAuthorityBlocked(bridgeRecoveryReason);
        }
        else if (answerTimeoutRuntimeUnlockOfferRecovery ||
                 peerResponseTimeoutRuntimeUnlockOfferRecovery)
        {
            bridgeRecoveryReason = "runtime_unlock_retry_authority_offer_blocked";
            MarkRuntimeUnlockRecoveryContractAuthorityBlocked(bridgeRecoveryReason);
        }

        recoveryReason = reason;
        recoverySessionId = sessionId;
        var accepted = RuntimeUnlockOfferSendRecoveryRequestOverrideForTests?.Invoke(this, reason, sessionId);
        if (accepted is null)
        {
            if (client is not RealNknClientAdapter realClient)
            {
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=tuna_activation_control_send_recovery_request_unsupported; session_id={SanitizeLogToken(sessionId ?? "none")}; purpose={normalizedPurpose}; trigger={SanitizeLogToken(trigger)}; reason={SanitizeLogToken(reason)}");
                return false;
            }

            accepted = realClient.RequestFileTransferReceiveStallRecovery(bridgeRecoveryReason);
        }

        LocalOperationalLog.Warn(
            "NKN.Tuna",
            $"event=tuna_activation_control_send_recovery_requested; session_id={SanitizeLogToken(sessionId ?? "none")}; purpose={normalizedPurpose}; trigger={SanitizeLogToken(trigger)}; reason={SanitizeLogToken(reason)}; bridge_reason={SanitizeLogToken(bridgeRecoveryReason)}; post_tuna_fallback_offer_recovery={(postTunaFallbackOfferRecovery ? 1 : 0)}; accepted={(accepted.Value ? 1 : 0)}");
        if (!accepted.Value &&
            ShouldJoinExistingRegularV4ReceiveStallRecoveryForRuntimeUnlock(
                regularV4RuntimeUnlockOfferRecovery,
                activeFileTransferRuntimeUnlockOfferRecovery,
                answerTimeoutRuntimeUnlockOfferRecovery,
                peerResponseTimeoutRuntimeUnlockOfferRecovery,
                sessionId))
        {
            MarkFileTransferTunaActivationBridgeRecoveryStarted(reason);
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_activation_control_send_recovery_joined_existing; session_id={SanitizeLogToken(sessionId ?? "none")}; purpose={normalizedPurpose}; trigger={SanitizeLogToken(trigger)}; reason={SanitizeLogToken(reason)}; existing_recovery=1; join_reason=regular_v4_receive_stall_recovery_in_progress");
            return true;
        }

        if (!accepted.Value &&
            ShouldJoinExistingFileTransferTunaActivationOfferSendRecovery(
                hasActivationPause,
                regularV4RuntimeUnlockOfferRecovery,
                activeFileTransferRuntimeUnlockOfferRecovery,
                postTunaFallbackOfferRecovery,
                activationSessionId,
                sessionId))
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_activation_control_send_recovery_joined_existing; session_id={SanitizeLogToken(sessionId ?? "none")}; purpose={normalizedPurpose}; trigger={SanitizeLogToken(trigger)}; reason={SanitizeLogToken(reason)}; existing_recovery=1");
            return true;
        }

        if (!accepted.Value)
        {
            return false;
        }

        MarkFileTransferTunaActivationBridgeRecoveryStarted(reason);
        return true;
    }

    private bool ShouldJoinExistingRegularV4ReceiveStallRecoveryForRuntimeUnlock(
        bool regularV4RuntimeUnlockOfferRecovery,
        bool activeFileTransferRuntimeUnlockOfferRecovery,
        bool answerTimeoutRuntimeUnlockOfferRecovery,
        bool peerResponseTimeoutRuntimeUnlockOfferRecovery,
        string? sessionId)
    {
        if (!regularV4RuntimeUnlockOfferRecovery &&
            !activeFileTransferRuntimeUnlockOfferRecovery &&
            !answerTimeoutRuntimeUnlockOfferRecovery &&
            !peerResponseTimeoutRuntimeUnlockOfferRecovery)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(sessionId) ||
            !HasActiveRegularV4FileTransferRouteHint(sessionId))
        {
            return false;
        }

        return client is RealNknClientAdapter realClient &&
               realClient.TryGetFileTransferRegularV4ActivationSendBlocker(
                   out var blockerReason,
                   out _,
                   includeRegularV4Pressure: false) &&
               IsReceiveStallRecoveryInProgressActivationSendBlocker(blockerReason);
    }

    private bool ShouldJoinExistingFileTransferTunaActivationOfferSendRecovery(
        bool hasActivationPause,
        bool regularV4RuntimeUnlockOfferRecovery,
        bool activeFileTransferRuntimeUnlockOfferRecovery,
        bool postTunaFallbackOfferRecovery,
        string? activationSessionId,
        string? recoverySessionId)
    {
        if (!IsFileTransferTunaActivationBridgeRecoveryActive())
        {
            return false;
        }

        if (hasActivationPause)
        {
            return true;
        }

        if (regularV4RuntimeUnlockOfferRecovery &&
            TryGetRegularV4RuntimeUnlockOfferRecoverySession(activationSessionId, out var regularSessionId))
        {
            return string.IsNullOrWhiteSpace(recoverySessionId) ||
                   string.Equals(regularSessionId, recoverySessionId, StringComparison.Ordinal);
        }

        if (activeFileTransferRuntimeUnlockOfferRecovery &&
            TryGetRuntimeUnlockOfferActiveFileTransferRecoverySession(activationSessionId, out var activeFileTransferSessionId))
        {
            return string.IsNullOrWhiteSpace(recoverySessionId) ||
                   string.Equals(activeFileTransferSessionId, recoverySessionId, StringComparison.Ordinal);
        }

        if (postTunaFallbackOfferRecovery)
        {
            var postTunaFallbackSessionId = string.IsNullOrWhiteSpace(activationSessionId)
                ? currentSessionSecurityState.SessionId?.Value
                : activationSessionId.Trim();
            return string.IsNullOrWhiteSpace(recoverySessionId) ||
                   string.Equals(postTunaFallbackSessionId, recoverySessionId, StringComparison.Ordinal);
        }

        if (!TryGetActivePostTunaFallbackRuntimeUnlockOfferRecoverySession(activationSessionId, out var activeSessionId) &&
            !TryGetPostTunaFallbackRuntimeUnlockOfferRecoverySession(activationSessionId, out activeSessionId))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(recoverySessionId) ||
               string.Equals(activeSessionId, recoverySessionId, StringComparison.Ordinal);
    }

    private bool TryGetActivePostTunaFallbackRuntimeUnlockOfferRecoverySession(
        string? requestedSessionId,
        out string? sessionId)
    {
        sessionId = string.IsNullOrWhiteSpace(requestedSessionId)
            ? currentSessionSecurityState.SessionId?.Value
            : requestedSessionId.Trim();
        if (string.IsNullOrWhiteSpace(sessionId) ||
            !IsCurrentRuntimeUnlockActivationOffer())
        {
            return false;
        }

        lock (accelerationGate)
        {
            if (string.IsNullOrWhiteSpace(outboundAccelerationOfferNonce) ||
                !IsRuntimeUnlockActivationReason(outboundAccelerationOfferTrigger) ||
                IsAccelerationNegotiatedAndHealthyUnsafe(sessionId))
            {
                return false;
            }
        }

        return TryGetUnresolvedFileTransferV6TransportEpochForCurrentSession(out var pendingEpoch) &&
               IsPostTunaFallbackRegularNknEpoch(pendingEpoch) &&
               ShouldAllowRuntimeUnlockRetryForActivePostTunaFallbackRepair(pendingEpoch);
    }

    private bool TryGetPostTunaFallbackRuntimeUnlockOfferRecoverySession(
        string? requestedSessionId,
        out string? sessionId)
    {
        sessionId = string.IsNullOrWhiteSpace(requestedSessionId)
            ? currentSessionSecurityState.SessionId?.Value
            : requestedSessionId.Trim();
        if (string.IsNullOrWhiteSpace(sessionId) ||
            !IsCurrentRuntimeUnlockActivationOffer())
        {
            return false;
        }

        lock (accelerationGate)
        {
            if (string.IsNullOrWhiteSpace(outboundAccelerationOfferNonce) ||
                !IsRuntimeUnlockActivationReason(outboundAccelerationOfferTrigger) ||
                IsAccelerationNegotiatedAndHealthyUnsafe(sessionId))
            {
                return false;
            }
        }

        if (TryGetUnresolvedFileTransferV6TransportEpochForCurrentSession(out var pendingEpoch) &&
            IsPostTunaFallbackRegularNknEpoch(pendingEpoch))
        {
            return true;
        }

        return HasActivePostTunaFallbackFileTransferRouteHint(sessionId);
    }

    private bool TryGetRuntimeUnlockOfferActiveFileTransferRecoverySession(
        string? requestedSessionId,
        out string? sessionId)
    {
        sessionId = string.IsNullOrWhiteSpace(requestedSessionId)
            ? currentSessionSecurityState.SessionId?.Value
            : requestedSessionId.Trim();
        if (string.IsNullOrWhiteSpace(sessionId) ||
            !IsCurrentRuntimeUnlockActivationOffer())
        {
            return false;
        }

        lock (accelerationGate)
        {
            if (string.IsNullOrWhiteSpace(outboundAccelerationOfferNonce) ||
                !IsRuntimeUnlockActivationReason(outboundAccelerationOfferTrigger) ||
                IsAccelerationNegotiatedAndHealthyUnsafe(sessionId))
            {
                return false;
            }
        }

        lock (gate)
        {
            foreach (var session in fileTransferDataSessions.Values)
            {
                if (!session.IsDisposed &&
                    string.Equals(session.SessionId, sessionId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return client is RealNknClientAdapter realClient &&
               realClient.HasActiveFileTransferRuntimeForActivationSend();
    }

    private bool TryGetRegularV4RuntimeUnlockOfferRecoverySession(
        string? requestedSessionId,
        out string? sessionId)
    {
        sessionId = string.IsNullOrWhiteSpace(requestedSessionId)
            ? currentSessionSecurityState.SessionId?.Value
            : requestedSessionId.Trim();
        if (string.IsNullOrWhiteSpace(sessionId) ||
            !IsCurrentRuntimeUnlockActivationOffer())
        {
            return false;
        }

        lock (accelerationGate)
        {
            if (string.IsNullOrWhiteSpace(outboundAccelerationOfferNonce) ||
                !IsRuntimeUnlockActivationReason(outboundAccelerationOfferTrigger) ||
                IsAccelerationNegotiatedAndHealthyUnsafe(sessionId))
            {
                return false;
            }
        }

        if (!HasActiveRegularV4FileTransferRouteHint(sessionId))
        {
            return false;
        }

        if (RuntimeUnlockOfferQueueAcceptedPressureOverrideForTests?.Invoke(this) == true)
        {
            return true;
        }

        return client is RealNknClientAdapter realClient &&
               realClient.TryGetFileTransferRegularV4ActivationSendBlocker(out var blockerReason, out _) &&
               (IsRegularV4PressureActivationSendBlocker(blockerReason) ||
                IsReceiveStallActivationSendBlocker(blockerReason));
    }

    private bool HasActiveRegularV4FileTransferRouteHint(string sessionId)
    {
        lock (gate)
        {
            foreach (var session in fileTransferDataSessions.Values)
            {
                if (session.IsDisposed ||
                    !string.Equals(session.SessionId, sessionId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!fileTransferRouteHints.TryGetValue(session.TransferId, out var routeHint))
                {
                    return true;
                }

                if (routeHint.Route == FileTransferRoute.RegularNknV4Fast)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsRegularV4PressureActivationSendBlocker(string? reason)
        => string.Equals(reason, "regular_v4_activation_send_pressure", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(reason, "regular_v4_control_feedback_pressure", StringComparison.OrdinalIgnoreCase);

    private static bool IsReceiveStallActivationSendBlocker(string? reason)
        => string.Equals(reason, "receive_stall_recovery_in_progress", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(reason, "receive_stall_recovery_awaiting_receive_proof", StringComparison.OrdinalIgnoreCase);

    private static bool IsReceiveStallRecoveryInProgressActivationSendBlocker(string? reason)
        => string.Equals(reason, "receive_stall_recovery_in_progress", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldDeferRuntimeUnlockSoftSettleForReceiveStallBlocker(string? reason, long remainingMs)
        => IsReceiveStallActivationSendBlocker(reason) && remainingMs > 0;

    private static bool ShouldBypassRuntimeUnlockReceiveStallAfterBoundedWait(string? reason)
        => string.Equals(reason, "receive_stall_recovery_awaiting_receive_proof", StringComparison.OrdinalIgnoreCase);

    private static string FormatElapsedMilliseconds(long startedTick)
    {
        if (startedTick <= 0)
        {
            return "-1";
        }

        return Math.Max(0, (long)Stopwatch.GetElapsedTime(startedTick).TotalMilliseconds)
            .ToString(CultureInfo.InvariantCulture);
    }

    private void ScheduleFileTransferTunaActivationPauseExpiry(string sessionId, long generation)
    {
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(FileTransferTunaActivationPauseMax, CancellationToken.None).ConfigureAwait(false);
                    var shouldResume = false;
                    lock (accelerationGate)
                    {
                        shouldResume =
                            generation == fileTransferTunaActivationPauseGeneration &&
                            string.Equals(fileTransferTunaActivationPauseSessionId, sessionId, StringComparison.Ordinal) &&
                            !IsAccelerationNegotiatedAndHealthyUnsafe(sessionId);
                    }

                    if (shouldResume)
                    {
                        ResumeFileTransferDataSessionsAfterTunaActivationNegotiation(
                            "tuna_activation_negotiation_pause_expired",
                            sessionId,
                            "pause_expiry");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=filetransfer_tuna_activation_negotiation_pause_expiry_failed; session_id={SanitizeLogToken(sessionId)}; error={ex.GetType().Name}");
                }
            },
            CancellationToken.None);
    }

    private bool IsAccelerationNegotiatedAndHealthyUnsafe(string? sessionId)
        => accelerationLane?.IsAvailable == true &&
           accelerationNegotiatedLanes != NknAccelerationLaneKind.None &&
           !string.IsNullOrWhiteSpace(accelerationSessionId) &&
           string.Equals(accelerationSessionId, sessionId, StringComparison.Ordinal);

    private void RebindScreenShareDataSessionsForTunaFallback(
        string reason,
        string? sessionId,
        NknAccelerationLaneKind lanes)
    {
        if ((lanes & NknAccelerationLaneKind.Screen) != NknAccelerationLaneKind.Screen ||
            string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var normalizedReason = SanitizeLogToken(reason);
        MarkTunaFallbackLaneState(
            sessionId,
            lane: NknAccelerationLaneKind.Screen,
            state: TunaFallbackLaneState.Pending,
            reason: normalizedReason);
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=screenshare_tuna_handoff_started; session_id={sessionId}; reason={normalizedReason}; lanes={FormatAccelerationLanesForLog(lanes)}");
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_disable_handoff_screen_started; session_id={sessionId}; reason={normalizedReason}; lanes={FormatAccelerationLanesForLog(lanes)}");
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_disable_handoff_media_pending; session_id={sessionId}; reason={normalizedReason}; lanes={FormatAccelerationLanesForLog(lanes)}");
        ScheduleScreenTunaHandoffWaitingMarker(sessionId, normalizedReason);
    }

    private void CompleteTunaFallbackProof(string reason)
    {
        TunaFallbackProofState? state;
        var normalizedReason = SanitizeLogToken(reason);
        var deferredPendingFileRoute = false;
        lock (accelerationGate)
        {
            state = tunaFallbackProofState;
            if (state is not null &&
                ShouldDeferTunaFallbackProofCompletionForPendingFileRouteUnsafe(state, normalizedReason))
            {
                deferredPendingFileRoute = true;
            }
            else
            {
                tunaFallbackProofState = null;
            }
        }

        if (state is null)
        {
            return;
        }

        var elapsedMs = Math.Max(0, (long)(DateTimeOffset.UtcNow - state.StartedUtc).TotalMilliseconds);
        if (deferredPendingFileRoute)
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_fallback_summary_deferred; session_id={state.SessionId}; fallback_epoch={state.Epoch}; reason={state.Reason}; deferred_reason={normalizedReason}; elapsed_ms={elapsedMs}; lanes={FormatAccelerationLanesForLog(state.Lanes)}; screen_state={FormatTunaFallbackLaneState(state.ScreenState)}; file_state={FormatTunaFallbackLaneState(state.FileState)}; file_v6_epoch_state={FormatTunaFallbackFileV6EpochState(state)}; file_v6_transport_epoch={state.FileV6TransportEpoch}; pending_file_route=1");
            return;
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_fallback_summary; session_id={state.SessionId}; fallback_epoch={state.Epoch}; reason={state.Reason}; completed_reason={normalizedReason}; elapsed_ms={elapsedMs}; lanes={FormatAccelerationLanesForLog(state.Lanes)}; screen_state={FormatTunaFallbackLaneState(state.ScreenState)}; file_state={FormatTunaFallbackLaneState(state.FileState)}; screen_nkn_frames_sent={state.ScreenNknFramesSent}; screen_nkn_frames_received={state.ScreenNknFramesReceived}; screen_frames_applied={state.ScreenFramesApplied}; file_nkn_frames_sent={state.FileNknFramesSent}; file_nkn_frames_received={state.FileNknFramesReceived}; control_nkn_messages_sent={state.ControlNknMessagesSent}; acceleration_used_after_fallback={(state.AccelerationUsedAfterFallback ? 1 : 0)}");
        if (IsMixedFallbackLaneSet(state.Lanes))
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_mixed_handoff_summary; session_id={state.SessionId}; fallback_epoch={state.Epoch}; reason={state.Reason}; completed_reason={normalizedReason}; elapsed_ms={elapsedMs}; screen_state={FormatTunaFallbackLaneState(state.ScreenState)}; file_state={FormatTunaFallbackLaneState(state.FileState)}; screen_frames_applied={state.ScreenFramesApplied}; file_nkn_frames_sent={state.FileNknFramesSent}; file_nkn_frames_received={state.FileNknFramesReceived}; control_nkn_messages_sent={state.ControlNknMessagesSent}");
        }

        NotifyTransportAccelerationStateChanged(reason);
    }

    private bool ShouldDeferTunaFallbackProofCompletionForPendingFileRouteUnsafe(
        TunaFallbackProofState state,
        string normalizedReason)
    {
        if ((state.Lanes & NknAccelerationLaneKind.File) != NknAccelerationLaneKind.File ||
            state.FileState is TunaFallbackLaneState.None or TunaFallbackLaneState.Recovered ||
            IsFinalTunaFallbackProofCompletionReason(normalizedReason))
        {
            return false;
        }

        return IsUserRequestedAccelerationStopReason(normalizedReason) ||
               IsRemoteUserRequestedAccelerationStopReason(normalizedReason) ||
               (!string.IsNullOrWhiteSpace(state.SessionId) &&
                (string.Equals(accelerationUserStoppedSessionId, state.SessionId, StringComparison.Ordinal) ||
                 string.Equals(accelerationPeerUserStoppedSessionId, state.SessionId, StringComparison.Ordinal)));
    }

    private static bool IsFinalTunaFallbackProofCompletionReason(string normalizedReason)
        => normalizedReason is "tuna_activation_started" or
            "dispose" or
            "disposed" or
            "reset_session_tracking" or
            "session_security_state_not_eligible";

    private void ConsumePostTunaFileFallbackRoute(string? sessionId, string? transferId, string reason)
    {
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId)
            ? currentSessionSecurityState.SessionId?.Value
            : sessionId.Trim();
        var normalizedTransferId = string.IsNullOrWhiteSpace(transferId) ? "none" : transferId.Trim();
        var normalizedReason = SanitizeLogToken(reason);
        TunaFallbackProofState? snapshot = null;
        TunaFallbackLaneState previousFileState = TunaFallbackLaneState.None;
        V6TransportEpochState? previousFileV6EpochState = null;
        long previousFileV6TransportEpoch = 0;
        bool clearedState = false;
        bool consumed = false;

        lock (accelerationGate)
        {
            if (tunaFallbackProofState is not { } state ||
                string.IsNullOrWhiteSpace(normalizedSessionId) ||
                !string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal) ||
                (state.Lanes & NknAccelerationLaneKind.File) != NknAccelerationLaneKind.File ||
                state.FileState == TunaFallbackLaneState.None)
            {
                return;
            }

            previousFileState = state.FileState;
            previousFileV6EpochState = state.FileV6EpochState;
            previousFileV6TransportEpoch = state.FileV6TransportEpoch;
            state.FileState = TunaFallbackLaneState.None;
            state.FileV6EpochState = null;
            state.FileV6TransportEpoch = 0;
            snapshot = state;
            consumed = true;

            var screenLaneStillNeedsProof =
                (state.Lanes & NknAccelerationLaneKind.Screen) == NknAccelerationLaneKind.Screen &&
                state.ScreenState is TunaFallbackLaneState.Pending or
                    TunaFallbackLaneState.MediaReady or
                    TunaFallbackLaneState.WaitingForRegularNkn;
            if (!screenLaneStillNeedsProof)
            {
                tunaFallbackProofState = null;
                clearedState = true;
            }
        }

        if (!consumed || snapshot is null)
        {
            return;
        }

        var previousFileV6EpochStateToken = previousFileV6EpochState is { } epochState
            ? FormatFileTransferV6TransportEpochStateForLog(epochState)
            : "none";
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=filetransfer_post_tuna_fallback_v6_route_consumed; session_id={SanitizeLogToken(normalizedSessionId ?? "none")}; transfer_id={SanitizeLogToken(normalizedTransferId)}; fallback_epoch={snapshot.Epoch}; reason={normalizedReason}; previous_file_state={FormatTunaFallbackLaneState(previousFileState)}; previous_file_v6_epoch_state={SanitizeLogToken(previousFileV6EpochStateToken)}; previous_file_v6_transport_epoch={previousFileV6TransportEpoch}; cleared_state={(clearedState ? 1 : 0)}; next_file_route={ResolveFileRouteAfterPostTunaFallbackClearedForLog()}");
        NotifyTransportAccelerationStateChanged(normalizedReason);
    }

    private string ResolveFileRouteAfterPostTunaFallbackClearedForLog()
        => FileTransferRouteResolver.Resolve(new FileTransferRouteResolverInput(
            IsFileTunaActive: IsFileTransferAccelerationNegotiatedAndHealthy(),
            IsPostTunaFileFallbackActive: false,
            IsDiagnosticRegularNknV6RouteEnabled: IsDiagnosticRegularNknV6RouteEnabledCore(),
            HandoffKind: FileTransferTransportHandoffKind.None,
            TransportProfileKind: FileTransferTransportProfileKind.Default)).TelemetryToken;

    private void MarkTunaFallbackAccelerationUsedAfterProof()
    {
        lock (accelerationGate)
        {
            if (tunaFallbackProofState is { } state)
            {
                state.AccelerationUsedAfterFallback = true;
            }
        }
    }

    private void RecordTunaFallbackNknFrameSent(MsgType messageType, NknBridgeChannel channel, int payloadBytes)
        => RecordTunaFallbackNknFrame(
            direction: "sent",
            messageType,
            channel,
            payloadBytes,
            currentSessionSecurityState.SessionId?.Value);

    private void RecordTunaFallbackNknFrameReceived(MsgType messageType, NknBridgeChannel channel, int payloadBytes, string? sessionId)
    {
        if (handlingTunaAcceleratedInboundMessage)
        {
            return;
        }

        RecordTunaFallbackNknFrame("received", messageType, channel, payloadBytes, sessionId);
    }

    private void RecordTunaFallbackFileTransferDataFrameSent(
        ReadOnlySpan<byte> payload,
        NknBridgeChannel channel,
        int payloadBytes,
        string? sessionId)
    {
        RecordTunaFallbackNknFrame("sent", MsgType.FileTransferDataFrame, channel, payloadBytes, sessionId);
        if (!TryDeserializeFileTransferDataFrameFromWire(payload, out var frame) ||
            frame is null)
        {
            return;
        }

        RecordPostTunaFallbackReceiverFrontierProofHint(frame, "sent", sessionId);

        if (!IsFileTransferFallbackNknProofPending() ||
            !TryMapPostTunaFileTransferFallbackNknProofKind(frame, "sent", out var proofKind))
        {
            return;
        }

        _ = CompleteFileTransferFallbackNknProofIfPending(proofKind, sessionId);
    }

    private void RecordTunaFallbackFileTransferDataFrameReceived(FileTransferDataFrame frame, NknBridgeChannel channel, int payloadBytes, string? sessionId)
    {
        if (handlingTunaAcceleratedInboundMessage)
        {
            return;
        }

        RecordTunaFallbackNknFrame("received", MsgType.FileTransferDataFrame, channel, payloadBytes, sessionId);
        RecordPostTunaFallbackReceiverFrontierProofHint(frame, "received", sessionId);
        if (TryMapFileTransferFallbackNknProofKind(frame, out var proofKind))
        {
            _ = CompleteFileTransferFallbackNknProofIfPending(proofKind, sessionId);
        }
    }

    private void RecordPostTunaFallbackReceiverFrontierProofHint(
        FileTransferDataFrame frame,
        string direction,
        string? sessionId)
    {
        if (!TryMapPostTunaFallbackReceiverFrontierProofKind(frame, out var proofKind))
        {
            return;
        }

        var normalizedTransferId = string.IsNullOrWhiteSpace(frame.TransferId)
            ? null
            : frame.TransferId.Trim();
        var normalizedSessionId = !string.IsNullOrWhiteSpace(frame.SessionId)
            ? frame.SessionId.Trim()
            : string.IsNullOrWhiteSpace(sessionId)
                ? null
                : sessionId.Trim();
        if (string.IsNullOrWhiteSpace(normalizedTransferId) ||
            string.IsNullOrWhiteSpace(normalizedSessionId))
        {
            return;
        }

        lock (gate)
        {
            if (!fileTransferDataSessions.TryGetValue(normalizedTransferId, out var session) ||
                session.IsDisposed ||
                !string.Equals(session.SessionId, normalizedSessionId, StringComparison.Ordinal) ||
                !fileTransferRouteHints.TryGetValue(normalizedTransferId, out var routeHint) ||
                routeHint.Route != FileTransferRoute.PostTunaFallbackV6)
            {
                return;
            }

            fileTransferPostTunaFallbackRepairProofHints[normalizedTransferId] =
                new FileTransferPostTunaFallbackRepairProofHint(
                    normalizedSessionId,
                    normalizedTransferId,
                    proofKind,
                    SanitizeLogToken(direction),
                    DateTimeOffset.UtcNow);
        }
    }

    private void RecordPostTunaFallbackControlPlaneProofHint(
        FileTransferControlPlaneDeliveryRequest request,
        LifecycleDeliveryResult result)
    {
        if (!result.AcceptedAny ||
            !result.PeerVisibleAny ||
            request.Kind is not (FileTransferControlPlaneKind.ReceiverState or FileTransferControlPlaneKind.FrontierRequest) ||
            !string.Equals(request.RouteToken, FileTransferRouteResolver.PostTunaFallbackV6Token, StringComparison.Ordinal) ||
            request.ProtocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            request.TransferLegGeneration <= 0 ||
            request.LiveRouteEpoch <= 0 ||
            request.Frame is not { } frame)
        {
            return;
        }

        var normalizedTransferId = string.IsNullOrWhiteSpace(frame.TransferId)
            ? null
            : frame.TransferId.Trim();
        var normalizedSessionId = string.IsNullOrWhiteSpace(frame.SessionId)
            ? null
            : frame.SessionId.Trim();
        if (string.IsNullOrWhiteSpace(normalizedTransferId) ||
            string.IsNullOrWhiteSpace(normalizedSessionId))
        {
            return;
        }

        var proofKind = request.Kind == FileTransferControlPlaneKind.ReceiverState
            ? "receiver_state_control_plane"
            : "frontier_request_control_plane";
        var proofDirection = request.Direction?.ToString().ToLowerInvariant() ?? "unknown";

        lock (gate)
        {
            if (!fileTransferDataSessions.TryGetValue(normalizedTransferId, out var session) ||
                session.IsDisposed ||
                !string.Equals(session.SessionId, normalizedSessionId, StringComparison.Ordinal) ||
                !fileTransferRouteHints.TryGetValue(normalizedTransferId, out var routeHint) ||
                routeHint.Route != FileTransferRoute.PostTunaFallbackV6)
            {
                return;
            }

            fileTransferPostTunaFallbackRepairProofHints[normalizedTransferId] =
                new FileTransferPostTunaFallbackRepairProofHint(
                    normalizedSessionId,
                    normalizedTransferId,
                    proofKind,
                    proofDirection,
                    DateTimeOffset.UtcNow);
        }

        MarkFileTransferFallbackLegAuthorityCompleted(
            normalizedSessionId,
            normalizedTransferId,
            request.RouteToken,
            request.ProtocolVersion,
            request.LiveRouteEpoch,
            request.TransferLegGeneration,
            request.BridgeRecoveryGeneration,
            request.TransportEpoch,
            request.CheckpointRequestId,
            request.Reason,
            proofKind,
            proofDirection);
    }

    private static bool TryMapPostTunaFallbackReceiverFrontierProofKind(
        FileTransferDataFrame frame,
        out string proofKind)
    {
        proofKind = frame switch
        {
            FileTransferReceiverStateFrameV6 => "receiver_state",
            FileTransferFrontierRequestFrameV6 => "frontier_request",
            _ => string.Empty,
        };

        return proofKind.Length > 0;
    }

    private void RecordTunaFallbackNknControlSent(MsgType messageType)
    {
        if (messageType is MsgType.ScreenShareFrame or MsgType.FileTransferDataFrame)
        {
            return;
        }

        lock (accelerationGate)
        {
            if (TryGetCurrentTunaFallbackProofStateUnsafe(currentSessionSecurityState.SessionId?.Value, out var state))
            {
                state.ControlNknMessagesSent++;
            }
        }
    }

    private void RecordTunaFallbackNknControlReceived(MsgType messageType, string? sessionId, int payloadBytes = 0)
    {
        if (messageType is MsgType.ScreenShareFrame or MsgType.FileTransferDataFrame)
        {
            return;
        }

        _ = CompleteFileTransferFallbackNknProofIfPending(
            $"nkn_control_{MapSecureMessageTypeForProof(messageType)}_received",
            sessionId);
    }

    private void RecordTunaFallbackNknFrame(
        string direction,
        MsgType messageType,
        NknBridgeChannel channel,
        int payloadBytes,
        string? sessionId)
    {
        if (!IsTunaFallbackProofFrame(messageType, channel))
        {
            return;
        }

        TunaFallbackProofState? snapshot;
        bool shouldLog;
        bool shouldLogScreenHandoffFrame = false;
        lock (accelerationGate)
        {
            if (!TryGetCurrentTunaFallbackProofStateUnsafe(sessionId, out var state))
            {
                shouldLog = false;
                snapshot = null;
            }
            else
            {
                if (direction == "sent")
                {
                    if (messageType == MsgType.ScreenShareFrame)
                    {
                        state.ScreenNknFramesSent++;
                    }
                    else
                    {
                        state.FileNknFramesSent++;
                    }
                }
                else
                {
                    if (messageType == MsgType.ScreenShareFrame)
                    {
                        state.ScreenNknFramesReceived++;
                    }
                    else
                    {
                        state.FileNknFramesReceived++;
                    }
                }

                if (messageType == MsgType.ScreenShareFrame)
                {
                    shouldLogScreenHandoffFrame = true;
                    if (state.ScreenState is TunaFallbackLaneState.Pending or TunaFallbackLaneState.WaitingForRegularNkn)
                    {
                        state.ScreenState = TunaFallbackLaneState.MediaReady;
                    }
                }

                shouldLog = ShouldLogTunaFallbackProofMarkerUnsafe(state, $"{direction}:{messageType}:{channel}", DateTimeOffset.UtcNow);
                snapshot = state;
            }
        }

        if (shouldLog && snapshot is not null)
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_fallback_nkn_frame_{direction}; message_type={MapTunaFallbackProofMessageType(messageType)}; channel={MapBridgeChannel(channel)}; session_id={snapshot.SessionId}; fallback_epoch={snapshot.Epoch}; payload_bytes={Math.Max(0, payloadBytes)}; reason={snapshot.Reason}");
            if (shouldLogScreenHandoffFrame && messageType == MsgType.ScreenShareFrame)
            {
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=screenshare_tuna_handoff_nkn_frame_{direction}; session_id={snapshot.SessionId}; fallback_epoch={snapshot.Epoch}; payload_bytes={Math.Max(0, payloadBytes)}; reason={snapshot.Reason}");
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=tuna_disable_handoff_media_ready; session_id={snapshot.SessionId}; reason={snapshot.Reason}; proof=screen_nkn_frame_{direction}; lanes={FormatAccelerationLanesForLog(snapshot.Lanes)}");
                LogMixedFallbackLaneState(snapshot, "screen_media_ready");
            }
        }
    }

    private void MarkFileTransferFallbackNknProofPending(
        string reason,
        string? sessionId,
        NknAccelerationLaneKind lanes,
        FileTransferReceiveRecoveryRequest? authorityRequest = null)
    {
        if ((lanes & NknAccelerationLaneKind.File) != NknAccelerationLaneKind.File)
        {
            return;
        }

        var normalizedReason = SanitizeLogToken(reason);
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId)
            ? currentSessionSecurityState.SessionId?.Value
            : sessionId.Trim();
        lock (fileTransferFallbackProofGate)
        {
            fileTransferFallbackProofPending = true;
            fileTransferFallbackProofReason = normalizedReason;
            fileTransferFallbackProofSessionId = normalizedSessionId;
            fileTransferFallbackProofLanes = lanes;
            fileTransferFallbackProofGeneration++;
            fileTransferFallbackProofProbeScheduled = false;
            fileTransferFallbackBulkProofObserved = false;
            fileTransferFallbackControlProofObserved = false;
            fileTransferFallbackProofTransferId = authorityRequest?.TransferId;
            fileTransferFallbackProofRouteToken = authorityRequest?.RouteToken;
            fileTransferFallbackProofProtocolVersion = authorityRequest?.ProtocolVersion ?? 0;
            fileTransferFallbackProofLiveRouteEpoch = authorityRequest?.LiveRouteEpoch ?? 0;
            fileTransferFallbackProofLegGeneration = authorityRequest?.TransferLegGeneration ?? 0;
            fileTransferFallbackProofBridgeRecoveryGeneration = authorityRequest?.BridgeRecoveryGeneration ?? 0;
            fileTransferFallbackProofTransportEpoch = authorityRequest?.TransportEpoch ?? 0;
            fileTransferFallbackProofCheckpointRequestId = authorityRequest?.CheckpointRequestId;
            fileTransferFallbackProofAuthorityReason = authorityRequest?.AuthorityReason;
        }

        var authorityFields = authorityRequest is null
            ? string.Empty
            : FormatFileTransferFallbackLegAuthorityFields(authorityRequest);
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=filetransfer_fallback_nkn_proof_pending; session_id={SanitizeLogToken(normalizedSessionId ?? "none")}; reason={normalizedReason}; lanes={FormatAccelerationLanesForLog(lanes)}{authorityFields}");
    }

    private bool ShouldUseFileTransferV6EpochForRegularNknRecovery(string? sessionId)
    {
        if (!HasActiveFileTransferDataSessionsForRecovery())
        {
            return false;
        }

        lock (accelerationGate)
        {
            if (TryGetCurrentTunaFallbackProofStateUnsafe(sessionId, out var state) &&
                (state.Lanes & NknAccelerationLaneKind.File) == NknAccelerationLaneKind.File &&
                state.FileState != TunaFallbackLaneState.Recovered)
            {
                return true;
            }
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        lock (fileTransferV6TransportEpochGate)
        {
            return lastRecoveredFileTransferV6RegularNknEpoch is { } recovered &&
                   string.Equals(recovered.SessionId, sessionId.Trim(), StringComparison.Ordinal) &&
                   recovered.TargetTransport == FileTransferTransportKind.RegularNkn &&
                   recovered.HandoffKind is FileTransferTransportHandoffKind.TunaToNormalFallback or
                       FileTransferTransportHandoffKind.RegularNknRecovery;
        }
    }

    private bool IsFileTransferFallbackNknProofPending()
    {
        lock (fileTransferFallbackProofGate)
        {
            return fileTransferFallbackProofPending;
        }
    }

    private bool CompleteFileTransferFallbackNknProofIfPending(string proofKind, string? sessionId)
    {
        string reason;
        string? pendingSessionId;
        NknAccelerationLaneKind lanes;
        bool bulkProofObserved;
        bool controlProofObserved;
        bool shouldLogUnconfirmed = false;
        bool requiresV6EpochRecovery = false;
        bool completed = false;
        string? authorityTransferId = null;
        string? authorityRouteToken = null;
        int authorityProtocolVersion = 0;
        int authorityLiveRouteEpoch = 0;
        int authorityLegGeneration = 0;
        int authorityBridgeRecoveryGeneration = 0;
        long authorityTransportEpoch = 0;
        string? authorityCheckpointRequestId = null;
        string? authorityReason = null;
        var normalizedProofKind = SanitizeLogToken(proofKind);
        var authoritativeProof = IsAuthoritativeFileTransferFallbackNknProof(normalizedProofKind);
        lock (fileTransferFallbackProofGate)
        {
            if (!fileTransferFallbackProofPending)
            {
                return false;
            }

            var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
            if (!string.IsNullOrWhiteSpace(fileTransferFallbackProofSessionId) &&
                !string.IsNullOrWhiteSpace(normalizedSessionId) &&
                !string.Equals(fileTransferFallbackProofSessionId, normalizedSessionId, StringComparison.Ordinal))
            {
                return false;
            }

            if (!authoritativeProof)
            {
                reason = fileTransferFallbackProofReason;
                pendingSessionId = fileTransferFallbackProofSessionId ?? normalizedSessionId;
                lanes = fileTransferFallbackProofLanes;
                shouldLogUnconfirmed = !fileTransferFallbackBulkProofObserved;
                fileTransferFallbackBulkProofObserved = true;
                bulkProofObserved = fileTransferFallbackBulkProofObserved;
                controlProofObserved = fileTransferFallbackControlProofObserved;
            }
            else
            {
                reason = fileTransferFallbackProofReason;
                pendingSessionId = fileTransferFallbackProofSessionId ?? normalizedSessionId;
                lanes = fileTransferFallbackProofLanes;
                fileTransferFallbackControlProofObserved = true;
                requiresV6EpochRecovery =
                    (lanes & NknAccelerationLaneKind.File) == NknAccelerationLaneKind.File &&
                    !IsPostTunaFileTransferFallbackNknProof(normalizedProofKind);
                bulkProofObserved = fileTransferFallbackBulkProofObserved;
                controlProofObserved = fileTransferFallbackControlProofObserved;
                if (requiresV6EpochRecovery)
                {
                    shouldLogUnconfirmed = true;
                }
                else
                {
                    authorityTransferId = fileTransferFallbackProofTransferId;
                    authorityRouteToken = fileTransferFallbackProofRouteToken;
                    authorityProtocolVersion = fileTransferFallbackProofProtocolVersion;
                    authorityLiveRouteEpoch = fileTransferFallbackProofLiveRouteEpoch;
                    authorityLegGeneration = fileTransferFallbackProofLegGeneration;
                    authorityBridgeRecoveryGeneration = fileTransferFallbackProofBridgeRecoveryGeneration;
                    authorityTransportEpoch = fileTransferFallbackProofTransportEpoch;
                    authorityCheckpointRequestId = fileTransferFallbackProofCheckpointRequestId;
                    authorityReason = fileTransferFallbackProofAuthorityReason;
                    fileTransferFallbackProofPending = false;
                    fileTransferFallbackProofReason = "none";
                    fileTransferFallbackProofSessionId = null;
                    fileTransferFallbackProofLanes = NknAccelerationLaneKind.None;
                    fileTransferFallbackProofGeneration++;
                    fileTransferFallbackProofProbeScheduled = false;
                    fileTransferFallbackBulkProofObserved = false;
                    fileTransferFallbackControlProofObserved = false;
                    ClearFileTransferFallbackProofAuthorityUnsafe();
                    completed = true;
                }
            }
        }

        if (!completed)
        {
            if (shouldLogUnconfirmed)
            {
                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=filetransfer_fallback_nkn_proof_unconfirmed; session_id={SanitizeLogToken(pendingSessionId ?? "none")}; reason={reason}; proof={normalizedProofKind}; lanes={FormatAccelerationLanesForLog(lanes)}; requires_control_proof={(controlProofObserved ? 0 : 1)}; requires_v6_epoch_recovery={(requiresV6EpochRecovery ? 1 : 0)}; bulk_seen={(bulkProofObserved ? 1 : 0)}; control_seen={(controlProofObserved ? 1 : 0)}");
                if (requiresV6EpochRecovery)
                {
                    LocalOperationalLog.Info(
                        "NKN.Tuna",
                        $"event=filetransfer_fallback_nkn_proof_waiting_for_v6_epoch; session_id={SanitizeLogToken(pendingSessionId ?? "none")}; reason={reason}; proof={normalizedProofKind}; lanes={FormatAccelerationLanesForLog(lanes)}");
                }
            }

            return false;
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=filetransfer_fallback_nkn_proof_observed; session_id={SanitizeLogToken(pendingSessionId ?? "none")}; reason={reason}; proof={normalizedProofKind}; lanes={FormatAccelerationLanesForLog(lanes)}; bulk_seen={(bulkProofObserved ? 1 : 0)}; control_seen={(controlProofObserved ? 1 : 0)}");
        if (authorityLegGeneration > 0)
        {
            MarkFileTransferFallbackLegAuthorityCompleted(
                pendingSessionId,
                authorityTransferId,
                authorityRouteToken,
                authorityProtocolVersion,
                authorityLiveRouteEpoch,
                authorityLegGeneration,
                authorityBridgeRecoveryGeneration,
                authorityTransportEpoch,
                authorityCheckpointRequestId,
                authorityReason,
                normalizedProofKind);
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_disable_handoff_nkn_ready; session_id={SanitizeLogToken(pendingSessionId ?? "none")}; reason={reason}; proof={normalizedProofKind}; lanes={FormatAccelerationLanesForLog(lanes)}");
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_disable_handoff_completed; session_id={SanitizeLogToken(pendingSessionId ?? "none")}; reason={reason}; proof={normalizedProofKind}; lanes={FormatAccelerationLanesForLog(lanes)}");

        if ((lanes & NknAccelerationLaneKind.File) == NknAccelerationLaneKind.File)
        {
            if (IsPostTunaFileTransferFallbackNknProof(normalizedProofKind))
            {
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=filetransfer_post_tuna_fallback_nkn_proved; session_id={SanitizeLogToken(pendingSessionId ?? "none")}; reason={reason}; proof={normalizedProofKind}; lanes={FormatAccelerationLanesForLog(lanes)}");
            }

            MarkTunaFallbackLaneState(
                pendingSessionId,
                lane: NknAccelerationLaneKind.File,
                state: TunaFallbackLaneState.Recovered,
                reason: reason);
            SetFileTransferDataSessionsAvailability(
                isAvailable: true,
                reason: "transport_recovered",
                requiresResumeRequest: true,
                handoffKind: FileTransferTransportHandoffKind.TunaToNormalFallback,
                targetTransport: FileTransferTransportKind.RegularNkn);
            if (IsPostTunaFileTransferFallbackNknProof(normalizedProofKind))
            {
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=filetransfer_post_tuna_fallback_cleanup_completed; session_id={SanitizeLogToken(pendingSessionId ?? "none")}; reason={reason}; proof={normalizedProofKind}; lanes={FormatAccelerationLanesForLog(lanes)}");
            }
        }

        return true;
    }

    private bool CompleteFileTransferFallbackNknProofFromV6Epoch(FileTransferV6TransportEpochSnapshot snapshot)
    {
        var reason = SanitizeLogToken(snapshot.Reason);
        var pendingSessionId = snapshot.SessionId;
        var lanes = NknAccelerationLaneKind.File;
        var bulkProofObserved = false;
        var controlProofObserved = true;
        var completedPendingProof = false;
        string? authorityTransferId = null;
        string? authorityRouteToken = null;
        int authorityProtocolVersion = 0;
        int authorityLiveRouteEpoch = 0;
        int authorityLegGeneration = 0;
        int authorityBridgeRecoveryGeneration = 0;
        long authorityTransportEpoch = 0;
        string? authorityCheckpointRequestId = null;
        string? authorityReason = null;
        lock (fileTransferFallbackProofGate)
        {
            if (fileTransferFallbackProofPending &&
                (fileTransferFallbackProofLanes & NknAccelerationLaneKind.File) == NknAccelerationLaneKind.File)
            {
                var normalizedSessionId = snapshot.SessionId.Trim();
                if (!string.IsNullOrWhiteSpace(fileTransferFallbackProofSessionId) &&
                    !string.Equals(fileTransferFallbackProofSessionId, normalizedSessionId, StringComparison.Ordinal))
                {
                    return false;
                }

                reason = fileTransferFallbackProofReason;
                pendingSessionId = fileTransferFallbackProofSessionId ?? normalizedSessionId;
                lanes = fileTransferFallbackProofLanes;
                bulkProofObserved = fileTransferFallbackBulkProofObserved;
                authorityTransferId = fileTransferFallbackProofTransferId;
                authorityRouteToken = fileTransferFallbackProofRouteToken;
                authorityProtocolVersion = fileTransferFallbackProofProtocolVersion;
                authorityLiveRouteEpoch = fileTransferFallbackProofLiveRouteEpoch;
                authorityLegGeneration = fileTransferFallbackProofLegGeneration;
                authorityBridgeRecoveryGeneration = fileTransferFallbackProofBridgeRecoveryGeneration;
                authorityTransportEpoch = fileTransferFallbackProofTransportEpoch;
                authorityCheckpointRequestId = fileTransferFallbackProofCheckpointRequestId;
                authorityReason = fileTransferFallbackProofAuthorityReason;
                completedPendingProof = true;
                fileTransferFallbackProofPending = false;
                fileTransferFallbackProofReason = "none";
                fileTransferFallbackProofSessionId = null;
                fileTransferFallbackProofLanes = NknAccelerationLaneKind.None;
                fileTransferFallbackProofGeneration++;
                fileTransferFallbackProofProbeScheduled = false;
                fileTransferFallbackBulkProofObserved = false;
                fileTransferFallbackControlProofObserved = false;
                ClearFileTransferFallbackProofAuthorityUnsafe();
            }
        }

        const string proofKind = "filetransfer_v6_epoch_recovered";
        if (completedPendingProof)
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=filetransfer_fallback_nkn_proof_observed; session_id={SanitizeLogToken(pendingSessionId ?? "none")}; reason={reason}; proof={proofKind}; lanes={FormatAccelerationLanesForLog(lanes)}; bulk_seen={(bulkProofObserved ? 1 : 0)}; control_seen={(controlProofObserved ? 1 : 0)}");
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_disable_handoff_nkn_ready; session_id={SanitizeLogToken(pendingSessionId ?? "none")}; reason={reason}; proof={proofKind}; lanes={FormatAccelerationLanesForLog(lanes)}");
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_disable_handoff_completed; session_id={SanitizeLogToken(pendingSessionId ?? "none")}; reason={reason}; proof={proofKind}; lanes={FormatAccelerationLanesForLog(lanes)}");
            if (authorityLegGeneration > 0)
            {
                MarkFileTransferFallbackLegAuthorityCompleted(
                    pendingSessionId,
                    authorityTransferId,
                    authorityRouteToken,
                    authorityProtocolVersion,
                    authorityLiveRouteEpoch,
                    authorityLegGeneration,
                    authorityBridgeRecoveryGeneration,
                    authorityTransportEpoch,
                    authorityCheckpointRequestId,
                    authorityReason,
                    proofKind);
            }
        }

        EnsureFileTransferFallbackRecoveredStateFromV6Epoch(snapshot, reason);
        MarkTunaFallbackLaneState(
            pendingSessionId,
            lane: NknAccelerationLaneKind.File,
            state: TunaFallbackLaneState.Recovered,
            reason: reason);
        SetFileTransferDataSessionsAvailability(
            isAvailable: true,
            reason: "transport_recovered",
            requiresResumeRequest: false,
            handoffKind: snapshot.HandoffKind,
            targetTransport: FileTransferTransportKind.RegularNkn);
        return completedPendingProof;
    }

    private bool EnsureFileTransferFallbackRecoveredStateFromV6Epoch(
        FileTransferV6TransportEpochSnapshot snapshot,
        string reason)
    {
        if (snapshot.HandoffKind != FileTransferTransportHandoffKind.TunaToNormalFallback ||
            snapshot.TargetTransport != FileTransferTransportKind.RegularNkn)
        {
            return false;
        }

        var sessionId = string.IsNullOrWhiteSpace(snapshot.SessionId)
            ? null
            : snapshot.SessionId.Trim();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        if (!HasActiveFileTransferDataSessionsForRecovery())
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=filetransfer_v6_fallback_recovered_state_synthesis_suppressed_no_active_transfer; session_id={SanitizeLogToken(sessionId)}; transfer_id={SanitizeLogToken(snapshot.TransferId)}; transport_epoch={snapshot.TransportEpoch}; reason={SanitizeLogToken(reason)}");
            return false;
        }

        TunaFallbackProofState? stateToLog = null;
        lock (accelerationGate)
        {
            if (TryGetCurrentTunaFallbackProofStateUnsafe(sessionId, out _))
            {
                return false;
            }

            var epoch = Interlocked.Increment(ref tunaFallbackProofNextEpoch);
            tunaFallbackProofState = new TunaFallbackProofState
            {
                Epoch = epoch,
                SessionId = sessionId,
                Reason = SanitizeLogToken(reason),
                StartedUtc = DateTimeOffset.UtcNow,
                Lanes = NknAccelerationLaneKind.File,
                FileState = TunaFallbackLaneState.Recovered,
                FileV6EpochState = V6TransportEpochState.Recovered,
                FileV6TransportEpoch = snapshot.TransportEpoch,
            };
            stateToLog = tunaFallbackProofState;
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=filetransfer_v6_fallback_recovered_state_synthesized; session_id={SanitizeLogToken(sessionId)}; fallback_epoch={stateToLog.Epoch}; reason={stateToLog.Reason}; transport_epoch={snapshot.TransportEpoch}; file_state={FormatTunaFallbackLaneState(stateToLog.FileState)}; file_v6_epoch_state={FormatTunaFallbackFileV6EpochState(stateToLog)}");
        NotifyTransportAccelerationStateChanged(reason);
        return true;
    }

    private bool TryGetFileTransferFallbackControlProofPendingSnapshot(out string? sessionId, out string reason, out NknAccelerationLaneKind lanes)
    {
        lock (fileTransferFallbackProofGate)
        {
            if (!fileTransferFallbackProofPending ||
                (fileTransferFallbackProofLanes & NknAccelerationLaneKind.File) != NknAccelerationLaneKind.File ||
                fileTransferFallbackControlProofObserved)
            {
                sessionId = null;
                reason = "none";
                lanes = NknAccelerationLaneKind.None;
                return false;
            }

            sessionId = fileTransferFallbackProofSessionId;
            reason = fileTransferFallbackProofReason;
            lanes = fileTransferFallbackProofLanes;
            return true;
        }
    }

    private static bool TryMapFileTransferFallbackNknProofKind(FileTransferDataFrame frame, out string proofKind)
    {
        proofKind = frame switch
        {
            FileTransferManifestFrameV4 and not FileTransferManifestFrameV6 => "file_transfer_v4_manifest_received",
            FileTransferStateFrameV4 and not FileTransferReceiverStateFrameV6 => "file_transfer_v4_state_frame_received",
            FileTransferChunkBatchFrameV4 and not FileTransferChunkBatchFrameV6 => "file_transfer_v4_bulk_frame_received",
            FileTransferPauseControlFrameV4 and not FileTransferPauseControlFrameV6 => "file_transfer_v4_pause_control_frame_received",
            FileTransferCompleteFrameV4 and not FileTransferCompleteFrameV6 => "file_transfer_v4_complete_frame_received",
            FileTransferCancelFrameV4 and not FileTransferCancelFrameV6 => "file_transfer_v4_cancel_frame_received",
            FileTransferErrorFrameV4 and not FileTransferErrorFrameV6 => "file_transfer_v4_error_frame_received",
            FileTransferReceiverStateFrameV6 => "file_transfer_v6_state_frame_received",
            FileTransferTransportEpochFrameV6 => "file_transfer_v6_transport_epoch_frame_received",
            FileTransferFrontierRequestFrameV6 => "file_transfer_v6_frontier_request_frame_received",
            FileTransferRepairProofFrameV6 => "file_transfer_v6_repair_proof_frame_received",
            FileTransferPauseControlFrameV6 => "file_transfer_v6_pause_control_frame_received",
            FileTransferCompleteFrameV6 => "file_transfer_v6_complete_frame_received",
            FileTransferCancelFrameV6 => "file_transfer_v6_cancel_frame_received",
            FileTransferErrorFrameV6 => "file_transfer_v6_error_frame_received",
            FileTransferChunkBatchFrameV6 => "file_transfer_bulk_frame_received",
            _ => string.Empty,
        };

        return proofKind.Length > 0;
    }

    private static bool TryMapPostTunaFileTransferFallbackNknProofKind(
        FileTransferDataFrame frame,
        string direction,
        out string proofKind)
    {
        var suffix = string.Equals(direction, "sent", StringComparison.Ordinal)
            ? "sent"
            : "received";
        proofKind = frame switch
        {
            FileTransferManifestFrameV4 and not FileTransferManifestFrameV6 => $"file_transfer_v4_manifest_{suffix}",
            FileTransferStateFrameV4 and not FileTransferReceiverStateFrameV6 => $"file_transfer_v4_state_frame_{suffix}",
            FileTransferChunkBatchFrameV4 and not FileTransferChunkBatchFrameV6 => $"file_transfer_v4_bulk_frame_{suffix}",
            FileTransferPauseControlFrameV4 and not FileTransferPauseControlFrameV6 => $"file_transfer_v4_pause_control_frame_{suffix}",
            FileTransferCompleteFrameV4 and not FileTransferCompleteFrameV6 => $"file_transfer_v4_complete_frame_{suffix}",
            FileTransferCancelFrameV4 and not FileTransferCancelFrameV6 => $"file_transfer_v4_cancel_frame_{suffix}",
            FileTransferErrorFrameV4 and not FileTransferErrorFrameV6 => $"file_transfer_v4_error_frame_{suffix}",
            _ => string.Empty,
        };

        return proofKind.Length > 0;
    }

    private static bool IsAuthoritativeFileTransferFallbackNknProof(string proofKind)
        => proofKind.StartsWith("nkn_control_", StringComparison.Ordinal) ||
           proofKind is
               "file_transfer_v4_manifest_received" or
               "file_transfer_v4_manifest_sent" or
               "file_transfer_v4_state_frame_received" or
               "file_transfer_v4_state_frame_sent" or
               "file_transfer_v4_bulk_frame_received" or
               "file_transfer_v4_bulk_frame_sent" or
               "file_transfer_v4_pause_control_frame_received" or
               "file_transfer_v4_pause_control_frame_sent" or
               "file_transfer_v4_complete_frame_received" or
               "file_transfer_v4_complete_frame_sent" or
               "file_transfer_v4_cancel_frame_received" or
               "file_transfer_v4_cancel_frame_sent" or
               "file_transfer_v4_error_frame_received" or
               "file_transfer_v4_error_frame_sent" or
               "file_transfer_v6_state_frame_received" or
               "file_transfer_v6_transport_epoch_frame_received" or
               "file_transfer_v6_frontier_request_frame_received" or
               "file_transfer_v6_repair_proof_frame_received" or
               "file_transfer_v6_pause_control_frame_received" or
               "file_transfer_v6_complete_frame_received" or
               "file_transfer_v6_cancel_frame_received" or
               "file_transfer_v6_error_frame_received";

    private static bool IsPostTunaFileTransferFallbackNknProof(string proofKind)
        => proofKind is
            "file_transfer_v6_state_frame_received" or
            "file_transfer_v6_transport_epoch_frame_received" or
            "file_transfer_v6_frontier_request_frame_received" or
            "file_transfer_v6_repair_proof_frame_received" or
            "file_transfer_v6_pause_control_frame_received" or
            "file_transfer_v6_complete_frame_received" or
            "file_transfer_v6_cancel_frame_received" or
            "file_transfer_v6_error_frame_received";

    private static string MapSecureMessageTypeForProof(MsgType messageType)
        => messageType switch
        {
            MsgType.Chat => "chat",
            MsgType.Ack => "ack",
            MsgType.SessionEnd => "session_end",
            MsgType.FileTransferCancel => "file_transfer_cancel",
            MsgType.FileTransferError => "file_transfer_error",
            MsgType.FileTransferComplete => "file_transfer_complete",
            MsgType.FileTransferPauseControl => "file_transfer_pause_control",
            MsgType.FileTransferHeartbeat => "file_transfer_heartbeat",
            MsgType.FileTransferTransportEpoch => "file_transfer_transport_epoch",
            MsgType.FileTransferTransportProbe => "file_transfer_transport_probe",
            MsgType.FileTransferRepairProof => "file_transfer_repair_proof",
            MsgType.FileTransferOffer => "file_transfer_offer",
            MsgType.FileTransferAccept => "file_transfer_accept",
            MsgType.FileTransferDecline => "file_transfer_decline",
            MsgType.FileTransferStart => "file_transfer_start",
            MsgType.FileTransferSessionOpen => "file_transfer_session_open",
            _ => SanitizeLogToken(messageType.ToString()).ToLowerInvariant(),
        };

    private void ScheduleFileTransferFallbackNknProbeIfPending(string trigger)
    {
        long generation;
        string reason;
        string? sessionId;
        NknAccelerationLaneKind lanes;
        lock (fileTransferFallbackProofGate)
        {
            if (!fileTransferFallbackProofPending ||
                fileTransferFallbackProofProbeScheduled ||
                (fileTransferFallbackProofLanes & NknAccelerationLaneKind.File) != NknAccelerationLaneKind.File)
            {
                return;
            }

            fileTransferFallbackProofProbeScheduled = true;
            generation = fileTransferFallbackProofGeneration;
            reason = fileTransferFallbackProofReason;
            sessionId = fileTransferFallbackProofSessionId;
            lanes = fileTransferFallbackProofLanes;
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=filetransfer_fallback_nkn_probe_scheduled; session_id={SanitizeLogToken(sessionId ?? "none")}; reason={SanitizeLogToken(reason)}; trigger={SanitizeLogToken(trigger)}; delay_ms={(long)FileTransferFallbackUnprovenProbeDelay.TotalMilliseconds}; lanes={FormatAccelerationLanesForLog(lanes)}");
        ArmPostTunaFallbackProofSendWindow(reason, trigger, sessionId);

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(FileTransferFallbackUnprovenProbeDelay).ConfigureAwait(false);
                    if (disposed)
                    {
                        return;
                    }

                    string probeReason;
                    string? probeSessionId;
                    NknAccelerationLaneKind probeLanes;
                    lock (fileTransferFallbackProofGate)
                    {
                        if (!fileTransferFallbackProofPending ||
                            fileTransferFallbackProofGeneration != generation ||
                            (fileTransferFallbackProofLanes & NknAccelerationLaneKind.File) != NknAccelerationLaneKind.File)
                        {
                            return;
                        }

                        probeReason = fileTransferFallbackProofReason;
                        probeSessionId = fileTransferFallbackProofSessionId;
                        probeLanes = fileTransferFallbackProofLanes;
                    }

                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=filetransfer_fallback_nkn_probe_started; session_id={SanitizeLogToken(probeSessionId ?? "none")}; reason={SanitizeLogToken(probeReason)}; trigger={SanitizeLogToken(trigger)}; delay_ms={(long)FileTransferFallbackUnprovenProbeDelay.TotalMilliseconds}; lanes={FormatAccelerationLanesForLog(probeLanes)}");
                    ArmPostTunaFallbackProofSendWindow(probeReason, $"probe_started:{trigger}", probeSessionId);
                    SetFileTransferDataSessionsAvailability(
                        isAvailable: false,
                        reason: "transport_recovered_unproven",
                        requiresResumeRequest: true,
                        handoffKind: ResolveFileTransferFallbackProbeHandoffKind(probeReason, trigger),
                        targetTransport: FileTransferTransportKind.RegularNkn);
                }
                catch (Exception ex)
                {
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=filetransfer_fallback_nkn_probe_failed; reason={SanitizeLogToken(reason)}; trigger={SanitizeLogToken(trigger)}; error={SanitizeLogToken(ex.GetType().Name)}");
                }
            });
    }

    private void ArmPostTunaFallbackProofSendWindow(string reason, string trigger, string? sessionId)
    {
        if (client is RealNknClientAdapter realClient)
        {
            realClient.ArmPostTunaFallbackProofSendWindow(reason, trigger, sessionId);
        }
    }

    private static FileTransferTransportHandoffKind ResolveFileTransferFallbackProbeHandoffKind(string? reason, string? trigger)
    {
        var normalizedReason = SanitizeLogToken(reason);
        var normalizedTrigger = SanitizeLogToken(trigger);
        return IsRegularNknRecoveryProbeToken(normalizedReason) ||
               IsRegularNknRecoveryProbeToken(normalizedTrigger)
            ? FileTransferTransportHandoffKind.RegularNknRecovery
            : FileTransferTransportHandoffKind.TunaToNormalFallback;
    }

    private static bool IsRegularNknRecoveryProbeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value is "receive_stall_recovery"
            or "receive_resumed_unproven"
            or "bridge_ready_unproven"
            or "bulk_receive_stalled"
            or "control_receive_stalled"
            or "all_channels_zero_receive" ||
            value.Contains("receive_stall", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryGetCurrentTunaFallbackProofStateUnsafe(string? sessionId, out TunaFallbackProofState state)
    {
        state = default!;
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? currentSessionSecurityState.SessionId?.Value : sessionId.Trim();
        if (tunaFallbackProofState is null ||
            string.IsNullOrWhiteSpace(normalizedSessionId) ||
            !string.Equals(tunaFallbackProofState.SessionId, normalizedSessionId, StringComparison.Ordinal))
        {
            return false;
        }

        state = tunaFallbackProofState;
        return true;
    }

    private void MarkTunaFallbackLaneState(
        string? sessionId,
        NknAccelerationLaneKind lane,
        TunaFallbackLaneState state,
        string reason)
    {
        TunaFallbackProofState? snapshot = null;
        var changed = false;
        lock (accelerationGate)
        {
            if (!TryGetCurrentTunaFallbackProofStateUnsafe(sessionId, out var current))
            {
                return;
            }

            if ((lane & NknAccelerationLaneKind.Screen) == NknAccelerationLaneKind.Screen &&
                current.ScreenState != state)
            {
                current.ScreenState = state;
                changed = true;
            }

            if ((lane & NknAccelerationLaneKind.File) == NknAccelerationLaneKind.File &&
                current.FileState != state)
            {
                current.FileState = state;
                changed = true;
            }

            snapshot = current;
        }

        if (changed && snapshot is not null)
        {
            LogMixedFallbackLaneState(snapshot, lane == NknAccelerationLaneKind.Screen
                ? "screen_" + FormatTunaFallbackLaneState(state)
                : "file_" + FormatTunaFallbackLaneState(state));
            NotifyTransportAccelerationStateChanged(reason);
        }
    }

    private void MarkTunaFallbackFileV6EpochState(
        string? sessionId,
        long transportEpoch,
        V6TransportEpochState state,
        string reason)
    {
        TunaFallbackProofState? snapshot = null;
        var changed = false;
        lock (accelerationGate)
        {
            if (!TryGetCurrentTunaFallbackProofStateUnsafe(sessionId, out var current))
            {
                return;
            }

            if (IsFileTransferFallbackFinalForLanes(current, NknAccelerationLaneKind.File) &&
                current.FileV6TransportEpoch > 0 &&
                transportEpoch > 0 &&
                transportEpoch < current.FileV6TransportEpoch)
            {
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=filetransfer_v6_epoch_state_ignored_final_fallback; session_id={SanitizeLogToken(sessionId ?? "none")}; transport_epoch={transportEpoch}; state={FormatFileTransferV6TransportEpochStateForLog(state)}; reason={SanitizeLogToken(reason)}; file_state={FormatTunaFallbackLaneState(current.FileState)}; file_v6_epoch_state={FormatTunaFallbackFileV6EpochState(current)}; file_v6_transport_epoch={current.FileV6TransportEpoch}");
                return;
            }

            if (IsFileTransferFallbackFinalForLanes(current, NknAccelerationLaneKind.File) &&
                state == V6TransportEpochState.TargetProofPending &&
                current.FileV6EpochState != state)
            {
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=filetransfer_v6_epoch_state_ignored_final_fallback; session_id={SanitizeLogToken(sessionId ?? "none")}; transport_epoch={transportEpoch}; state={FormatFileTransferV6TransportEpochStateForLog(state)}; reason={SanitizeLogToken(reason)}; file_state={FormatTunaFallbackLaneState(current.FileState)}; file_v6_epoch_state={FormatTunaFallbackFileV6EpochState(current)}; file_v6_transport_epoch={current.FileV6TransportEpoch}");
                return;
            }

            if (current.FileV6EpochState != state ||
                current.FileV6TransportEpoch != transportEpoch)
            {
                current.FileV6EpochState = state;
                current.FileV6TransportEpoch = transportEpoch;
                changed = true;
            }

            snapshot = current;
        }

        if (changed && snapshot is not null)
        {
            LogMixedFallbackLaneState(snapshot, "file_v6_epoch_" + FormatFileTransferV6TransportEpochStateForLog(state));
        }
    }

    private void ScheduleScreenTunaHandoffWaitingMarker(string sessionId, string reason)
    {
        var fallbackEpoch = 0L;
        lock (accelerationGate)
        {
            if (!TryGetCurrentTunaFallbackProofStateUnsafe(sessionId, out var state) ||
                (state.Lanes & NknAccelerationLaneKind.Screen) != NknAccelerationLaneKind.Screen)
            {
                return;
            }

            fallbackEpoch = state.Epoch;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                    TunaFallbackProofState? snapshot = null;
                    var shouldLog = false;
                    lock (accelerationGate)
                    {
                        if (TryGetCurrentTunaFallbackProofStateUnsafe(sessionId, out var state) &&
                            state.Epoch == fallbackEpoch &&
                            state.ScreenState is TunaFallbackLaneState.Pending or TunaFallbackLaneState.MediaReady)
                        {
                            state.ScreenState = TunaFallbackLaneState.WaitingForRegularNkn;
                            snapshot = state;
                            shouldLog = true;
                        }
                    }

                    if (!shouldLog || snapshot is null)
                    {
                        return;
                    }

                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=screenshare_tuna_handoff_waiting_for_regular_nkn; session_id={snapshot.SessionId}; fallback_epoch={snapshot.Epoch}; reason={SanitizeLogToken(reason)}; screen_state={FormatTunaFallbackLaneState(snapshot.ScreenState)}");
                    LogMixedFallbackLaneState(snapshot, "screen_waiting_for_regular_nkn");
                }
                catch
                {
                    // Best-effort diagnostics only.
                }
            },
            CancellationToken.None);
    }

    private void MarkScreenTunaHandoffFrameApplied(ScreenShareVideoFrameReadyEventArgs e)
    {
        TunaFallbackProofState? snapshot = null;
        var recovered = false;
        lock (accelerationGate)
        {
            if (!TryGetCurrentTunaFallbackProofStateUnsafe(e.SessionId, out var state) ||
                (state.Lanes & NknAccelerationLaneKind.Screen) != NknAccelerationLaneKind.Screen)
            {
                return;
            }

            state.ScreenFramesApplied++;
            if (state.ScreenState != TunaFallbackLaneState.Recovered)
            {
                state.ScreenState = TunaFallbackLaneState.Recovered;
                recovered = true;
            }

            snapshot = state;
        }

        if (snapshot is null)
        {
            return;
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=screenshare_tuna_handoff_nkn_frame_applied; session_id={snapshot.SessionId}; fallback_epoch={snapshot.Epoch}; stream_epoch={e.StreamEpoch}; frame_id={e.FrameId}; is_keyframe={(e.IsKeyFrame ? 1 : 0)}; reason={snapshot.Reason}");
        if (recovered)
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=screenshare_tuna_handoff_recovered; session_id={snapshot.SessionId}; fallback_epoch={snapshot.Epoch}; stream_epoch={e.StreamEpoch}; frame_id={e.FrameId}; elapsed_ms={Math.Max(0, (long)(DateTimeOffset.UtcNow - snapshot.StartedUtc).TotalMilliseconds)}; reason={snapshot.Reason}");
            LogMixedFallbackLaneState(snapshot, "screen_recovered");
        }
    }

    private bool ShouldIgnoreAcceleratedScreenShareFrameDuringFallback(string? sessionId)
    {
        if (!handlingTunaAcceleratedInboundMessage || IsAccelerationNegotiatedAndHealthy())
        {
            return false;
        }

        lock (accelerationGate)
        {
            return TryGetCurrentTunaFallbackProofStateUnsafe(sessionId, out var state) &&
                   (state.Lanes & NknAccelerationLaneKind.Screen) == NknAccelerationLaneKind.Screen &&
                   state.ScreenState != TunaFallbackLaneState.None;
        }
    }

    private void LogAcceleratedScreenShareFrameIgnoredDuringFallback(string? sessionId, long streamEpoch, long frameId)
    {
        TunaFallbackProofState? snapshot = null;
        var shouldLog = false;
        lock (accelerationGate)
        {
            if (!TryGetCurrentTunaFallbackProofStateUnsafe(sessionId, out var state))
            {
                return;
            }

            shouldLog = ShouldLogTunaFallbackProofMarkerUnsafe(state, "stale_accelerated_screen_frame_ignored", DateTimeOffset.UtcNow);
            snapshot = state;
        }

        if (!shouldLog || snapshot is null)
        {
            return;
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=screenshare_tuna_handoff_stale_accelerated_frame_ignored; session_id={snapshot.SessionId}; fallback_epoch={snapshot.Epoch}; stream_epoch={streamEpoch}; frame_id={frameId}; screen_state={FormatTunaFallbackLaneState(snapshot.ScreenState)}; reason={snapshot.Reason}");
    }

    private static bool IsMixedFallbackLaneSet(NknAccelerationLaneKind lanes)
        => (lanes & NknAccelerationLaneKind.Screen) == NknAccelerationLaneKind.Screen &&
           (lanes & NknAccelerationLaneKind.File) == NknAccelerationLaneKind.File;

    private static string FormatTunaFallbackLaneState(TunaFallbackLaneState state)
        => state switch
        {
            TunaFallbackLaneState.Pending => "pending",
            TunaFallbackLaneState.MediaReady => "media_ready",
            TunaFallbackLaneState.Recovered => "recovered",
            TunaFallbackLaneState.WaitingForRegularNkn => "waiting_for_regular_nkn",
            _ => "none",
        };

    private static void LogMixedFallbackLaneState(TunaFallbackProofState state, string laneState)
    {
        if (!IsMixedFallbackLaneSet(state.Lanes))
        {
            return;
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_mixed_handoff_lane_state_changed; session_id={state.SessionId}; fallback_epoch={state.Epoch}; reason={state.Reason}; lane_state={SanitizeLogToken(laneState)}; screen_state={FormatTunaFallbackLaneState(state.ScreenState)}; file_state={FormatTunaFallbackLaneState(state.FileState)}; file_v6_epoch_state={FormatTunaFallbackFileV6EpochState(state)}; file_v6_transport_epoch={state.FileV6TransportEpoch}");
    }

    private static string FormatTunaFallbackFileV6EpochState(TunaFallbackProofState state)
        => state.FileV6EpochState is { } epochState
            ? FormatFileTransferV6TransportEpochStateForLog(epochState)
            : state.FileState == TunaFallbackLaneState.Recovered
                ? "unknown"
                : FormatTunaFallbackLaneState(state.FileState);

    private static bool ShouldLogTunaFallbackProofMarkerUnsafe(TunaFallbackProofState state, string key, DateTimeOffset now)
    {
        if (!state.LogStates.TryGetValue(key, out var logState))
        {
            state.LogStates[key] = new TunaFallbackProofLogState
            {
                LastLoggedUtc = now,
            };
            return true;
        }

        logState.CountSinceLastLog++;
        if (logState.CountSinceLastLog < TunaFallbackProofLogEveryFrames &&
            now - logState.LastLoggedUtc < TunaFallbackProofLogWindow)
        {
            return false;
        }

        logState.CountSinceLastLog = 0;
        logState.LastLoggedUtc = now;
        return true;
    }

    private static bool IsTunaFallbackProofFrame(MsgType messageType, NknBridgeChannel channel)
        => (messageType == MsgType.ScreenShareFrame && channel == NknBridgeChannel.Media) ||
           (messageType == MsgType.FileTransferDataFrame && channel == NknBridgeChannel.Bulk);

    private static string MapTunaFallbackProofMessageType(MsgType messageType)
        => messageType switch
        {
            MsgType.ScreenShareFrame => "screenshare_frame",
            MsgType.FileTransferDataFrame => "file_transfer_data_frame",
            _ => MapEnvelopeTypeForDiagnostics(messageType),
        };

    internal static bool ShouldStartTunaFallbackProofForResetReason(string? reason)
    {
        var normalized = SanitizeLogToken(reason);
        if (normalized is "(none)" or
            "dispose" or
            "disposed" or
            "reset_session_tracking" or
            "session_security_state_not_eligible")
        {
            return false;
        }

        if (IsRemoteUserRequestedAccelerationStopReason(normalized))
        {
            return false;
        }

        if (normalized.StartsWith("remote_", StringComparison.Ordinal))
        {
            return true;
        }

        if (IsUserRequestedAccelerationStopReason(normalized))
        {
            return false;
        }

        return normalized is
            "cap_reached" or
            "byte_cap_reached" or
            "duration_cap_reached" or
            "sidecar_read_failed" or
            "sidecar_write_failed" or
            "sidecar_remote_closed" or
            "sidecar_queue_overflow" or
            "sidecar_status_timeout" or
            "sidecar_invalid_status" or
            "sidecar_status_parse_failed" or
            "sidecar_local_ipc_eof" or
            "sidecar_tuna_stream_eof" or
            "sidecar_local_write_failed" or
            "sidecar_tuna_write_failed" or
            "sidecar_byte_cap_reached" or
            "sidecar_duration_cap_reached" or
            "sidecar_remote_byte_cap_reached" or
            "sidecar_remote_duration_cap_reached" or
            "sidecar_provider_timeout" or
            "sidecar_listener_exited" or
            "sidecar_dialer_exited" or
            "sidecar_process_exited" or
            "sidecar_unexpected_exit";
    }

    internal static string NormalizeAccelerationSidecarResetReason(string? reason)
    {
        var normalized = SanitizeLogToken(reason);
        return normalized.StartsWith("sidecar_", StringComparison.Ordinal)
            ? normalized
            : $"sidecar_{normalized}";
    }

    internal static bool ShouldStartImmediateFileTransferFallbackProbe(string? reason)
    {
        var normalized = SanitizeLogToken(reason);
        if (IsUserRequestedAccelerationStopReason(normalized) ||
            IsRemoteUserRequestedAccelerationStopReason(normalized))
        {
            return false;
        }

        return normalized is
            "cap_reached" or
            "byte_cap_reached" or
            "duration_cap_reached" or
            "sidecar_read_failed" or
            "sidecar_write_failed" or
            "sidecar_remote_closed" or
            "sidecar_queue_overflow" or
            "sidecar_status_timeout" or
            "sidecar_invalid_status" or
            "sidecar_status_parse_failed" or
            "sidecar_local_ipc_eof" or
            "sidecar_tuna_stream_eof" or
            "sidecar_local_write_failed" or
            "sidecar_tuna_write_failed" or
            "sidecar_provider_timeout" or
            "sidecar_listener_exited" or
            "sidecar_dialer_exited" or
            "sidecar_process_exited" or
            "sidecar_unexpected_exit" or
            "remote_cap_reached" or
            "remote_byte_cap_reached" or
            "remote_duration_cap_reached" or
            "remote_read_failed" or
            "remote_write_failed" or
            "remote_closed" or
            "remote_sidecar_read_failed" or
            "remote_sidecar_write_failed" or
            "remote_sidecar_remote_closed" or
            "remote_sidecar_local_ipc_eof" or
            "remote_sidecar_tuna_stream_eof" or
            "remote_sidecar_local_write_failed" or
            "remote_sidecar_tuna_write_failed" or
            "sidecar_byte_cap_reached" or
            "sidecar_duration_cap_reached" or
            "sidecar_remote_byte_cap_reached" or
            "sidecar_remote_duration_cap_reached" or
            "remote_sidecar_byte_cap_reached" or
            "remote_sidecar_duration_cap_reached";
    }

    internal static bool ShouldCompleteTunaFallbackProofForResetReason(string? reason)
    {
        var normalized = SanitizeLogToken(reason);
        return normalized is
            "dispose" or
            "disposed" or
            "reset_session_tracking" or
            "session_security_state_not_eligible";
    }

    private static INknAccelerationLane? CreateAccelerationLane(
        NknTunaAccelerationOptions options,
        INknTunaListenerSidecarSupervisor? listenerSupervisor = null)
    {
        if (options is null || !options.Enabled)
        {
            return null;
        }

        return new NknTunaAccelerationLane(options, listenerSupervisor);
    }

    private void OnAccelerationStateChanged(object? sender, AccelerationStateChangedEventArgs e)
    {
        var reason = SanitizeLogToken(e.Reason);
        var diagnostics = accelerationLane?.GetDiagnosticsSnapshot() ?? NknAccelerationLaneDiagnostics.Empty;
        var downSessionId = string.Empty;
        var downLanes = NknAccelerationLaneKind.None;
        var rearmSessionId = string.Empty;
        var rearmRetryReason = string.Empty;
        var rearmRecoveryReason = string.Empty;
        var shouldRearmRuntimeUnlockListener = !e.IsAvailable &&
                                               TryCaptureRuntimeUnlockListenerRearmAfterSidecarDrop(
                                                   e.Reason,
                                                   out rearmSessionId,
                                                   out rearmRetryReason,
                                                   out rearmRecoveryReason);
        var shouldNotifyRemoteDown = !e.IsAvailable &&
                                     ShouldNotifyRemoteAccelerationDown(e.Reason) &&
                                     TryCaptureAccelerationNegotiation(out downSessionId, out downLanes);
        var shouldRetryEarlyDrop = !e.IsAvailable &&
                                   ShouldRetryEarlyAccelerationDrop(e.Reason, diagnostics) &&
                                   TryCaptureAccelerationNegotiation(out _, out _);
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_state_changed; available={(e.IsAvailable ? 1 : 0)}; reason={reason}");
        if (!e.IsAvailable)
        {
            FailRuntimeUnlockTunaPathLeaseIfCurrent(
                currentSessionSecurityState.SessionId?.Value,
                offerGeneration: 0,
                NormalizeAccelerationSidecarResetReason(e.Reason),
                failTransaction: false);
            ResetAccelerationNegotiation(NormalizeAccelerationSidecarResetReason(e.Reason));
            if (shouldNotifyRemoteDown)
            {
                ScheduleAccelerationDownNotification(downSessionId, downLanes, reason);
            }

            if (shouldRetryEarlyDrop)
            {
                ScheduleAccelerationEarlyDropRetry(reason, diagnostics);
            }

            if (shouldRearmRuntimeUnlockListener)
            {
                ScheduleRuntimeUnlockListenerRearmAfterSidecarDrop(
                    rearmSessionId,
                    rearmRetryReason,
                    rearmRecoveryReason,
                    reason);
            }

            return;
        }

        NotifyTransportAccelerationStateChanged(e.Reason);
    }

    private bool TryCaptureRuntimeUnlockListenerRearmAfterSidecarDrop(
        string? reason,
        out string sessionId,
        out string retryReason,
        out string recoveryReason)
    {
        sessionId = currentSessionSecurityState.SessionId?.Value ?? string.Empty;
        retryReason = "runtime_unlock_listener_sidecar_unavailable";
        recoveryReason = NormalizeAccelerationSidecarResetReason(reason);
        if (string.IsNullOrWhiteSpace(sessionId) ||
            !IsRuntimeUnlockListenerRearmSidecarDropReason(reason) ||
            IsAccelerationUserStoppedForCurrentSession() ||
            accelerationLane is not INknTunaAccelerationSession { CanOfferListener: true } ||
            !IsSessionAccelerationEligible(out _))
        {
            return false;
        }

        var normalizedSessionId = sessionId.Trim();
        return HasActiveRegularV4FileTransferRouteHint(normalizedSessionId) ||
               HasActivePostTunaFallbackFileTransferRouteHint(normalizedSessionId) ||
               HasActiveFileTransferDataSessionForSession(normalizedSessionId);
    }

    private static bool IsRuntimeUnlockListenerRearmSidecarDropReason(string? reason)
    {
        var normalized = SanitizeLogToken(reason);
        return normalized is
            "read_failed" or
            "write_failed" or
            "remote_closed" or
            "sidecar_read_failed" or
            "sidecar_write_failed" or
            "sidecar_remote_closed" or
            "sidecar_local_ipc_eof" or
            "sidecar_tuna_stream_eof" or
            "sidecar_local_write_failed" or
            "sidecar_tuna_write_failed" or
            "sidecar_listener_exited" or
            "sidecar_process_exited" or
            "sidecar_unexpected_exit";
    }

    private void ScheduleRuntimeUnlockListenerRearmAfterSidecarDrop(
        string sessionId,
        string retryReason,
        string recoveryReason,
        string trigger)
    {
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? string.Empty : sessionId.Trim();
        if (string.IsNullOrWhiteSpace(normalizedSessionId))
        {
            return;
        }

        LocalOperationalLog.Warn(
            "NKN.Tuna",
            $"event=tuna_acceleration_runtime_unlock_listener_rearm_after_sidecar_drop; session_id={SanitizeLogToken(normalizedSessionId)}; retry_reason={SanitizeLogToken(retryReason)}; recovery_reason={SanitizeLogToken(recoveryReason)}; trigger={SanitizeLogToken(trigger)}");
        RetirePendingAccelerationAnswerAckForRuntimeUnlockListenerRearm(
            normalizedSessionId,
            recoveryReason,
            trigger);
        ArmRuntimeUnlockRetryAfterRecovery(
            0,
            normalizedSessionId,
            retryReason,
            recoveryReason,
            requiresLocalListenerRetry: true);
    }

    private void OnAccelerationMessageReceived(object? sender, NknIncomingMessage e)
    {
        if (disposed || e.Payload.Length == 0)
        {
            return;
        }

        var source = ResolveSyntheticAccelerationSource(e.Channel);
        if (string.IsNullOrWhiteSpace(source))
        {
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("tuna_source_unavailable");
            LocalOperationalLog.Warn("NKN.Tuna", $"event=tuna_accelerated_frame_rejected; reason=source_unavailable; channel={MapBridgeChannel(e.Channel)}");
            return;
        }

        var previousAcceleratedInbound = handlingTunaAcceleratedInboundMessage;
        handlingTunaAcceleratedInboundMessage = true;
        try
        {
            OnClientMessageReceived(
                sender,
                new NknIncomingMessage(
                    source,
                    e.Payload,
                    isTopic: false,
                    topic: null,
                    channel: e.Channel,
                    bridgeIngressObservedUtcMs: e.BridgeIngressObservedUtcMs,
                    bridgeMessageObservedUtcMs: e.BridgeMessageObservedUtcMs,
                    binaryFrameDecodedUtcMs: e.BinaryFrameDecodedUtcMs,
                    socketDataEventEmittedUtcMs: e.SocketDataEventEmittedUtcMs,
                    wsReceiverWriteEnteredUtcMs: e.WsReceiverWriteEnteredUtcMs,
                    wsMessageEmittedUtcMs: e.WsMessageEmittedUtcMs,
                    sdkHandleMsgEnteredUtcMs: e.SdkHandleMsgEnteredUtcMs,
                    clientMessageDispatchUtcMs: e.ClientMessageDispatchUtcMs,
                    multiClientMessageDispatchUtcMs: e.MultiClientMessageDispatchUtcMs));
        }
        finally
        {
            handlingTunaAcceleratedInboundMessage = previousAcceleratedInbound;
        }
    }

    private string? ResolveSyntheticAccelerationSource(NknBridgeChannel channel)
        => channel switch
        {
            NknBridgeChannel.Media => ResolveExpectedRemoteMediaPeerAddressForCurrentSession(),
            NknBridgeChannel.Bulk => ResolveExpectedRemoteBulkPeerAddressForCurrentSession(),
            _ => ResolveExpectedRemotePeerAddressForCurrentSession(),
        };

    private void ScheduleAccelerationNegotiationIfEligible(string reason)
    {
        var isRuntimeUnlockActivation = IsRuntimeUnlockActivationReason(reason);
        if (disposed ||
            accelerationLane is not INknTunaAccelerationSession ||
            IsAccelerationUserStoppedForCurrentSession() ||
            !IsSessionAccelerationEligible(out _))
        {
            return;
        }

        if (HasPendingOutboundAccelerationOffer() || HasPendingAccelerationAnswerAck())
        {
            return;
        }

        if (Interlocked.CompareExchange(ref accelerationNegotiationScheduled, 1, 0) != 0)
        {
            if (isRuntimeUnlockActivation)
            {
                if (TrySupersedeStaleActiveNegotiationForRuntimeUnlockRecoveryContract(reason) &&
                    Interlocked.CompareExchange(ref accelerationNegotiationScheduled, 1, 0) == 0)
                {
                    NotifyTransportAccelerationStateChanged($"negotiation_scheduled_{reason}");
                    _ = Task.Run(
                        async () =>
                        {
                            try
                            {
                                MarkRuntimeUnlockRecoveryContractRetryDispatched(reason);
                                await TrySendAccelerationOfferAsync(
                                        reason,
                                        ResolvePayerDecisionIdForNegotiation(reason),
                                        CancellationToken.None)
                                    .ConfigureAwait(false);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                LocalOperationalLog.Warn(
                                    "NKN.Tuna",
                                    $"event=tuna_acceleration_negotiation_failed; reason={SanitizeLogToken(reason)}; error={ex.GetType().Name}");
                            }
                            finally
                            {
                                Interlocked.Exchange(ref accelerationNegotiationScheduled, 0);
                                if (Interlocked.Exchange(ref pendingRuntimeUnlockAccelerationNegotiation, 0) != 0)
                                {
                                    ScheduleAccelerationNegotiationIfEligible("runtime_unlock");
                                }
                            }
                        },
                        CancellationToken.None);
                    return;
                }

                Interlocked.Exchange(ref pendingRuntimeUnlockAccelerationNegotiation, 1);
                MarkRuntimeUnlockRecoveryContractQueuedBehindActiveNegotiation(reason);
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_runtime_unlock_queued_behind_active_negotiation; reason={SanitizeLogToken(reason)}");
            }

            return;
        }

        var payerDecisionId = ResolvePayerDecisionIdForNegotiation(reason);
        NotifyTransportAccelerationStateChanged($"negotiation_scheduled_{reason}");
        _ = Task.Run(
            async () =>
            {
                try
                {
                    MarkRuntimeUnlockRecoveryContractRetryDispatched(reason);
                    await TrySendAccelerationOfferAsync(reason, payerDecisionId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_negotiation_failed; reason={SanitizeLogToken(reason)}; error={ex.GetType().Name}");
                }
                finally
                {
                    Interlocked.Exchange(ref accelerationNegotiationScheduled, 0);
                    if (Interlocked.Exchange(ref pendingRuntimeUnlockAccelerationNegotiation, 0) != 0)
                    {
                        ScheduleAccelerationNegotiationIfEligible("runtime_unlock");
                    }
                }
            },
            CancellationToken.None);
    }

    private async Task TrySendAccelerationOfferAsync(string reason, long payerDecisionId, CancellationToken ct)
    {
        if (IsStaleLocalPayerDecision(payerDecisionId))
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_negotiation_stale; stage=preflight; payer_decision_id={payerDecisionId}; current_payer_decision_id={Volatile.Read(ref accelerationPayerDecisionId)}; reason={SanitizeLogToken(reason)}");
            return;
        }

        if (accelerationLane is not INknTunaAccelerationSession tunaSession)
        {
            RejectAccelerationOfferPreflight(reason, "missing_tuna_session", retryable: false);
            return;
        }

        if (string.IsNullOrWhiteSpace(remoteEndpoint))
        {
            RejectAccelerationOfferPreflight(reason, "missing_remote_endpoint", retryable: true);
            return;
        }

        if (!IsSessionAccelerationEligible(out var eligibleLanes))
        {
            RejectAccelerationOfferPreflight(reason, "session_not_eligible", retryable: false);
            return;
        }

        var preflightLanes = eligibleLanes & tunaSession.ConfiguredLanes;
        if (preflightLanes == NknAccelerationLaneKind.None)
        {
            RejectAccelerationOfferPreflight(reason, "no_eligible_lane", retryable: false, eligibleLanes);
            NotifyTransportAccelerationStateChanged("no_eligible_lane");
            return;
        }

        var sessionId = currentSessionSecurityState.SessionId?.Value;
        if (string.IsNullOrWhiteSpace(sessionId) || !TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            RejectAccelerationOfferPreflight(reason, "missing_secure_session_context", retryable: true, preflightLanes);
            return;
        }

        var localRole = ResolveLocalSessionRole();
        var pausedForActivationNegotiation = false;
        void PauseActivationIfNeeded(string pauseTrigger)
        {
            if (pausedForActivationNegotiation)
            {
                return;
            }

            if (IsAccelerationNegotiatedAndHealthy())
            {
                return;
            }

            pausedForActivationNegotiation = PauseFileTransferDataSessionsForTunaActivationNegotiation(
                "activation_negotiation_pending",
                sessionId,
                pauseTrigger);
        }

        void ResumeActivationPauseIfNeeded(string resumeReason, string resumeTrigger)
        {
            if (!pausedForActivationNegotiation)
            {
                return;
            }

            if (string.Equals(resumeReason, "tuna_activation_failed_regular_v4_resumed", StringComparison.Ordinal))
            {
                ResumeFileTransferDataSessionsAfterTunaActivationFailure(
                    resumeTrigger,
                    sessionId,
                    resumeTrigger);
            }
            else
            {
                ResumeFileTransferDataSessionsAfterTunaActivationNegotiation(
                    resumeReason,
                    sessionId,
                    resumeTrigger);
            }

            pausedForActivationNegotiation = false;
        }

        var isRuntimeUnlockActivation = IsRuntimeUnlockActivationReason(reason);
        var requiresContractLocalListenerRetry = false;

        try
        {
            requiresContractLocalListenerRetry = isRuntimeUnlockActivation &&
                RequiresLocalListenerRetryForRuntimeUnlockRecoveryContract(sessionId, reason);
            if (requiresContractLocalListenerRetry && !tunaSession.CanOfferListener)
            {
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=session_recovery_contract_listener_rearm_required; session_id={SanitizeLogToken(sessionId)}; reason={SanitizeLogToken(reason)}; can_offer_listener=0");
            }

            var payerIntentQueued = await SendAccelerationPayerIntentAsync(
                    remoteEndpoint,
                    sessionId,
                    envelopeCode,
                    localRole,
                    preflightLanes,
                    tunaSession.CanOfferListener || requiresContractLocalListenerRetry,
                    reason,
                    payerDecisionId,
                    ct)
                .ConfigureAwait(false);
            if (!payerIntentQueued)
            {
                ResumeActivationPauseIfNeeded("tuna_activation_failed_regular_v4_resumed", "payer_intent_send_rejected");
                ScheduleAccelerationNegotiationRetry("payer_intent_send_rejected");
                return;
            }
        }
        catch
        {
            ResumeActivationPauseIfNeeded("tuna_activation_failed_regular_v4_resumed", "payer_intent_send_failed");
            throw;
        }

        if (!tunaSession.CanOfferListener && !requiresContractLocalListenerRetry)
        {
            ResumeActivationPauseIfNeeded("tuna_activation_failed_regular_v4_resumed", "listener_unavailable");
            RejectAccelerationOfferPreflight(reason, "listener_unavailable", retryable: true, eligibleLanes);
            return;
        }

        NotifyTransportAccelerationStateChanged("checking_payer_priority");
        if (await ShouldSuppressLocalPaidOfferForHelpeePriorityAsync(localRole, reason, ct).ConfigureAwait(false))
        {
            ResumeActivationPauseIfNeeded("tuna_activation_failed_regular_v4_resumed", "payer_priority_suppressed");
            return;
        }

        if (IsStaleLocalPayerDecision(payerDecisionId))
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_negotiation_stale; stage=after_payer_priority; payer_decision_id={payerDecisionId}; current_payer_decision_id={Volatile.Read(ref accelerationPayerDecisionId)}; reason={SanitizeLogToken(reason)}");
            ResumeActivationPauseIfNeeded("stale_payer_decision", "after_payer_priority_stale");
            return;
        }

        if (accelerationLane is not INknTunaAccelerationSession ||
            !IsSessionAccelerationEligible(out eligibleLanes))
        {
            ResumeActivationPauseIfNeeded("session_not_eligible_after_payer_priority", "session_not_eligible_after_payer_priority");
            RejectAccelerationOfferPreflight(reason, "session_not_eligible_after_payer_priority", retryable: false);
            return;
        }

        if (IsAccelerationUserStoppedForCurrentSession())
        {
            ResumeActivationPauseIfNeeded("user_stopped_tuna", "after_payer_priority_user_stopped");
            RejectAccelerationOfferPreflight(reason, "user_stopped_tuna", retryable: false, eligibleLanes);
            return;
        }

        preflightLanes = eligibleLanes & tunaSession.ConfiguredLanes;
        if (preflightLanes == NknAccelerationLaneKind.None)
        {
            ResumeActivationPauseIfNeeded("no_eligible_lane", "after_payer_priority_no_eligible_lane");
            RejectAccelerationOfferPreflight(reason, "no_eligible_lane", retryable: false, eligibleLanes);
            NotifyTransportAccelerationStateChanged("no_eligible_lane");
            return;
        }

        NotifyTransportAccelerationStateChanged("selected_payer_starting_listener");
        NotifyTransportAccelerationStateChanged("listener_starting");
        var runtimeUnlockTunaPathLease = RuntimeUnlockTunaPathLeaseSnapshot.None;
        if (isRuntimeUnlockActivation)
        {
            lock (accelerationGate)
            {
                runtimeUnlockTunaPathLease = StartRuntimeUnlockTunaPathLeaseUnsafe(
                    sessionId,
                    payerDecisionId,
                    reason);
            }
        }

        if (isRuntimeUnlockActivation &&
            ShouldPauseRegularNknV4FileTransferForRuntimeUnlock(sessionId))
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=filetransfer_tuna_activation_negotiation_regular_nkn_pause_deferred; session_id={SanitizeLogToken(sessionId)}; reason=runtime_unlock_listener_starting; trigger={SanitizeLogToken(reason)}");
        }

        if (!await tunaSession.EnsureListenerSidecarConnectedAsync(remoteEndpoint, ct).ConfigureAwait(false) ||
            string.IsNullOrWhiteSpace(tunaSession.LocalTunaAddress))
        {
            NotifyTransportAccelerationStateChanged("listener_sidecar_unavailable");
            if (isRuntimeUnlockActivation)
            {
                FailRuntimeUnlockTunaPathLeaseIfCurrent(
                    sessionId,
                    offerGeneration: 0,
                    "listener_sidecar_unavailable",
                    failTransaction: false);
            }

            ResumeActivationPauseIfNeeded("tuna_activation_failed_regular_v4_resumed", "listener_sidecar_unavailable");
            var listenerRetryReason = requiresContractLocalListenerRetry
                ? "runtime_unlock_listener_rearm_failed"
                : "listener_sidecar_unavailable";
            if (requiresContractLocalListenerRetry)
            {
                MarkRuntimeUnlockRecoveryContractAuthorityBlocked(listenerRetryReason);
                if (TryDeferRuntimeUnlockListenerRearmRetryForActiveRegularV4Recovery(
                        sessionId,
                        listenerRetryReason))
                {
                    return;
                }

                FailRuntimeUnlockRecoveryContractIfActive(sessionId, listenerRetryReason);
                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=session_recovery_contract_listener_rearm_failed; session_id={SanitizeLogToken(sessionId)}; reason={SanitizeLogToken(listenerRetryReason)}; trigger={SanitizeLogToken(reason)}");
                return;
            }

            ScheduleAccelerationNegotiationRetry(listenerRetryReason);
            return;
        }

        if (isRuntimeUnlockActivation)
        {
            MarkRuntimeUnlockTunaPathLeaseListenerReady(
                runtimeUnlockTunaPathLease,
                sessionId,
                reason);
        }

        if (requiresContractLocalListenerRetry || HasRuntimeUnlockRecoveryContractPendingCutThrough(sessionId))
        {
            MarkRuntimeUnlockRecoveryContractListenerRearmCompleted(sessionId, reason);
        }

        NotifyTransportAccelerationStateChanged("listener_ready");
        if (IsStaleLocalPayerDecision(payerDecisionId))
        {
            NotifyTransportAccelerationStateChanged("suppressed_by_peer_payer");
            ResumeActivationPauseIfNeeded("stale_payer_decision", "listener_ready_stale_payer_decision");
            try
            {
                await tunaSession.StopAsync("stale_payer_decision", ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_stop_failed; reason=stale_payer_decision; error={ex.GetType().Name}");
            }

            return;
        }

        if (IsAccelerationUserStoppedForCurrentSession())
        {
            NotifyTransportAccelerationStateChanged("user_stopped_tuna");
            ResumeActivationPauseIfNeeded("user_stopped_tuna", "listener_ready_user_stopped");
            try
            {
                await tunaSession.StopAsync("user_stopped_tuna", ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_stop_failed; reason=user_stopped_tuna; error={ex.GetType().Name}");
            }

            return;
        }

        var offeredLanes = eligibleLanes & tunaSession.SupportedLanes;
        if (offeredLanes == NknAccelerationLaneKind.None)
        {
            NotifyTransportAccelerationStateChanged("no_supported_lane");
            ResumeActivationPauseIfNeeded("tuna_activation_failed_regular_v4_resumed", "no_supported_lane");
            ScheduleAccelerationNegotiationRetry("listener_sidecar_unavailable");
            return;
        }

        if (isRuntimeUnlockActivation &&
            TryDeferRuntimeUnlockOfferDispatchForRegularV4ReceiveRecovery(
                sessionId,
                reason,
                payerDecisionId,
                out _,
                out _))
        {
            return;
        }

        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var sentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var offer = new TransportAccelerationOfferPayload
        {
            SessionId = sessionId,
            SenderRole = localRole,
            TunaAddress = tunaSession.LocalTunaAddress,
            SupportedLanes = NknAccelerationLaneCodec.ToNames(offeredLanes),
            Trigger = SanitizeLogToken(reason),
            PayerDecisionId = payerDecisionId,
            SentAtUnixMs = sentAtUnixMs,
            ExpiresAtUnixMs = sentAtUnixMs + (long)AccelerationOfferLifetime.TotalMilliseconds,
            Nonce = nonce,
            SidecarProtocolVersion = TunaSidecarProtocolVersion,
        };
        var payload = CreateSecureControlPayload(
            MsgType.TransportAccelerationOffer,
            nonce,
            JsonSerializer.SerializeToUtf8Bytes(offer));
        var envelope = CreateEnvelope(envelopeCode, MsgType.TransportAccelerationOffer, payload, replyTo: null);
        long offerGeneration;
        RuntimeUnlockRecoveryRetryState? runtimeUnlockAuthorityGrantedSnapshot = null;
        RuntimeUnlockRecoveryRetryState? runtimeUnlockAuthorityOfferWindowSnapshot = null;
        RuntimeUnlockTransactionDecision? runtimeUnlockTransactionStarted = null;
        lock (accelerationGate)
        {
            outboundAccelerationOfferNonce = nonce;
            outboundAccelerationOfferTrigger = SanitizeLogToken(reason);
            outboundAccelerationOfferPayerDecisionId = payerDecisionId;
            offerGeneration = ++outboundAccelerationOfferGeneration;
            runtimeUnlockOfferProofState = isRuntimeUnlockActivation
                ? new RuntimeUnlockOfferProofState
                {
                    Generation = offerGeneration,
                    Nonce = nonce,
                    SessionId = sessionId,
                    PayerDecisionId = payerDecisionId,
                    Trigger = SanitizeLogToken(reason),
                    CreatedUtcMs = sentAtUnixMs,
                }
                : null;
            if (isRuntimeUnlockActivation)
            {
                runtimeUnlockTransactionStarted = StartRuntimeUnlockTransactionForOfferUnsafe(
                    sessionId,
                    offerGeneration,
                    reason);
                if (runtimeUnlockTunaPathLease.LeaseGeneration > 0)
                {
                    runtimeUnlockTunaPathLease = BindRuntimeUnlockTunaPathLeaseToOfferUnsafe(
                        runtimeUnlockTunaPathLease,
                        runtimeUnlockTransactionStarted.State.TransactionGeneration,
                        offerGeneration,
                        payerDecisionId,
                        reason);
                    runtimeUnlockTransactionStarted = runtimeUnlockTransactionStarted with
                    {
                        State = AttachRuntimeUnlockTunaPathLeaseUnsafe(runtimeUnlockTransactionStarted.State),
                    };
                }

                if (runtimeUnlockRecoveryRetryState is { } state &&
                    state.ContractState is not (SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed) &&
                    string.Equals(state.SessionId, sessionId, StringComparison.Ordinal))
                {
                    state.CurrentOfferGeneration = offerGeneration;
                    if (!state.RetryAuthorityGranted &&
                        state.RetryDispatched)
                    {
                        GrantRuntimeUnlockRecoveryContractRetryAuthorityUnsafe(state);
                        runtimeUnlockAuthorityGrantedSnapshot = state;
                    }

                    if (state.RetryAuthorityGranted && state.RetryAuthorityPending)
                    {
                        RefreshRuntimeUnlockRecoveryContractOfferSendWindowUnsafe(state, sentAtUnixMs);
                        runtimeUnlockAuthorityOfferWindowSnapshot = state;
                    }
                }
                else if (runtimeUnlockRecoveryRetryState is
                         {
                             ContractState: SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed
                         })
                {
                    runtimeUnlockRecoveryRetryState = null;
                }

                ClearRuntimeUnlockQueueAcceptedObservedEscapeLocked();
            }
        }

        LogRuntimeUnlockTransactionDecision(
            RuntimeUnlockTransactionEventKind.OfferGenerationCreated,
            runtimeUnlockTransactionStarted);

        if (requiresContractLocalListenerRetry)
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=runtime_unlock_offer_dispatched_after_listener_rearm; session_id={SanitizeLogToken(sessionId)}; payer_decision_id={payerDecisionId}; generation={offerGeneration}; trigger={SanitizeLogToken(reason)}");
        }

        if (runtimeUnlockAuthorityGrantedSnapshot is not null)
        {
            LogRuntimeUnlockRecoveryContract(
                "session_recovery_contract_retry_authority_granted",
                runtimeUnlockAuthorityGrantedSnapshot.SessionId);
        }

        if (runtimeUnlockAuthorityOfferWindowSnapshot is not null)
        {
            LogRuntimeUnlockRecoveryContract(
                "session_recovery_contract_retry_authority_offer_window_refreshed",
                runtimeUnlockAuthorityOfferWindowSnapshot.SessionId);
        }

        if (!isRuntimeUnlockActivation)
        {
            PauseActivationIfNeeded("offer_send_prepare");
        }
        else if (IsFileTransferUsingRegularNknFallbackForCurrentSession())
        {
            PauseActivationIfNeeded("post_tuna_fallback_offer_send_prepare");
        }

        var runtimeUnlockCutThroughStarted = isRuntimeUnlockActivation &&
            TryStartRuntimeUnlockCutThroughForCurrentOffer(
                sessionId,
                offerGeneration,
                reason);
        if (runtimeUnlockCutThroughStarted)
        {
            PauseActivationIfNeeded("runtime_unlock_cutthrough_offer_send_prepare");
        }

        AccelerationControlSendResult offerSend;
        try
        {
            offerSend = await SendAccelerationControlEnvelopeWithBulkBypassAsync(
                    remoteEndpoint,
                    envelope,
                    "offer",
                    ct,
                    requireObservedSend: true,
                    activationSessionId: sessionId,
                    onQueueAccepted: () =>
                    {
                        if (!isRuntimeUnlockActivation && ShouldPauseRuntimeUnlockOfferOnQueueAccepted())
                        {
                            PauseActivationIfNeeded("offer_queue_accepted");
                        }
                    },
                    onObservedSendWaitStarted: isRuntimeUnlockActivation
                        ? null
                        : () => PauseActivationIfNeeded("offer_queue_accepted"),
                    queueAcceptedAsObservedReason: isRuntimeUnlockActivation
                        ? GetRuntimeUnlockOfferQueueAcceptedObservedReason
                        : null)
                .ConfigureAwait(false);
        }
        catch
        {
            ResumeActivationPauseIfNeeded("tuna_activation_failed_regular_v4_resumed", "offer_send_failed");
            throw;
        }

        if (isRuntimeUnlockActivation &&
            IsRuntimeUnlockOfferGenerationRetired(nonce, payerDecisionId, offerGeneration))
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_offer_send_result_ignored; reason=retired_generation; trigger={SanitizeLogToken(reason)}; session_id={SanitizeLogToken(sessionId)}; payer_decision_id={payerDecisionId}; generation={offerGeneration}; observed_lane={SanitizeLogToken(offerSend.ObservedLane)}; succeeded={(offerSend.Succeeded ? 1 : 0)}");
            return;
        }

        if (offerSend.Succeeded)
        {
            MarkRuntimeUnlockOfferObservedSendIfCurrent(nonce, payerDecisionId, offerGeneration, offerSend.ObservedLane);
            if (isRuntimeUnlockActivation)
            {
                ApplyRuntimeUnlockTransaction(
                    RuntimeUnlockTransactionEventKind.ObservedSend,
                    sessionId,
                    offerGeneration,
                    "offer_observed_send",
                    observedLane: offerSend.ObservedLane);
                MarkRuntimeUnlockRecoveryContractRetryObserved(sessionId, offerGeneration, offerSend.ObservedLane);
                if (runtimeUnlockCutThroughStarted)
                {
                    MarkRuntimeUnlockCutThroughOfferSent(sessionId, offerGeneration, offerSend.ObservedLane);
                }

                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=filetransfer_tuna_activation_negotiation_regular_nkn_pause_deferred; session_id={SanitizeLogToken(sessionId)}; reason=runtime_unlock_offer_observed_waiting_for_answer; trigger={SanitizeLogToken(reason)}");

                ScheduleRuntimeUnlockPeerVisibleOfferDeliveryConfirmation(
                    remoteEndpoint,
                    envelope,
                    sessionId,
                    payerDecisionId,
                    nonce,
                    offerGeneration,
                    reason);
            }
        }

        if (isRuntimeUnlockActivation && offerSend.UntrustedProbeSent)
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_acceleration_offer_probe_sent_untrusted; reason={SanitizeLogToken(reason)}; session_id={SanitizeLogToken(sessionId)}; lanes={string.Join(",", offer.SupportedLanes)}; payer_decision_id={payerDecisionId}; generation={offerGeneration}; observed_lane={SanitizeLogToken(offerSend.ObservedLane)}; queue_local_only=0; replay_scheduled=0; answer_timeout_scheduled=1");
            NotifyTransportAccelerationStateChanged("waiting_for_answer_untrusted_probe");
            MarkRuntimeUnlockOfferAnswerTimeoutScheduledIfCurrent(nonce, payerDecisionId, offerGeneration);
            ScheduleAccelerationOfferAnswerTimeout(nonce);
            return;
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_offer_{(offerSend.Succeeded ? "queued" : "rejected")}; reason={SanitizeLogToken(reason)}; session_id={sessionId}; lanes={string.Join(",", offer.SupportedLanes)}; payer_decision_id={payerDecisionId}; generation={offerGeneration}; observed_lane={SanitizeLogToken(offerSend.ObservedLane)}; queue_local_only=0; recovery_requested={(offerSend.RecoveryRequested ? 1 : 0)}; recovery_reason={SanitizeLogToken(offerSend.RecoveryReason)}");
        if (!offerSend.Succeeded)
        {
            if (isRuntimeUnlockActivation)
            {
                var promoteSendBlockToCutThrough =
                    ShouldPromoteRuntimeUnlockAuthoritySendBlockToCutThrough(sessionId);
                var retryReason = promoteSendBlockToCutThrough
                    ? RuntimeUnlockCutThroughRetryReason
                    : "runtime_unlock_offer_send_not_observed";
                var effectiveRecoveryReason = promoteSendBlockToCutThrough
                    ? RuntimeUnlockCutThroughRecoveryReason
                    : offerSend.RecoveryReason ?? "tuna_activation_offer_send_timeout";
                var retired = RetireRuntimeUnlockOfferIfCurrent(
                    nonce,
                    payerDecisionId,
                    offerGeneration,
                    sessionId,
                    "offer_send_not_observed",
                    retryReason);
                var alreadyRetiredForRecovery = !retired &&
                    IsRuntimeUnlockOfferGenerationRetired(nonce, payerDecisionId, offerGeneration);
                var deferRetryUntilRecoverySettled = offerSend.RecoveryRequested &&
                    (retired || alreadyRetiredForRecovery);
                if (promoteSendBlockToCutThrough)
                {
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_runtime_unlock_authority_send_blocked_promoted_to_cutthrough; session_id={SanitizeLogToken(sessionId)}; payer_decision_id={payerDecisionId}; generation={offerGeneration}; retry_reason={SanitizeLogToken(retryReason)}; recovery_reason={SanitizeLogToken(effectiveRecoveryReason)}; source_recovery_reason={SanitizeLogToken(offerSend.RecoveryReason)}");
                }

                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_activation_offer_not_observed; trigger={SanitizeLogToken(reason)}; session_id={SanitizeLogToken(sessionId)}; payer_decision_id={payerDecisionId}; generation={offerGeneration}; late_answer_window_ms={(long)AccelerationOfferAnswerTimeout.TotalMilliseconds}; retry_scheduled={(deferRetryUntilRecoverySettled ? 0 : 1)}; retry_after_recovery_armed={(deferRetryUntilRecoverySettled ? 1 : 0)}; replay_scheduled=0; answer_timeout_scheduled=0; pause_deferred={(pausedForActivationNegotiation ? 0 : 1)}; recovery_requested={(offerSend.RecoveryRequested ? 1 : 0)}; recovery_reason={SanitizeLogToken(effectiveRecoveryReason)}");
                NotifyTransportAccelerationStateChanged("activation_offer_not_observed");
                if (pausedForActivationNegotiation)
                {
                    ResumeActivationPauseIfNeeded(
                        "tuna_activation_failed_regular_v4_resumed",
                        "offer_send_not_observed");
                }

                if (deferRetryUntilRecoverySettled)
                {
                    if (retired || (promoteSendBlockToCutThrough && alreadyRetiredForRecovery))
                    {
                        ArmRuntimeUnlockRetryAfterRecovery(
                            offerGeneration,
                            offerSend.RecoverySessionId ?? sessionId,
                            retryReason,
                            effectiveRecoveryReason,
                            requiresLocalListenerRetry: !promoteSendBlockToCutThrough ||
                                                        !ShouldUseListenerReadyFastRetry(RuntimeUnlockCutThroughRetryReason));
                    }
                    else
                    {
                        LocalOperationalLog.Info(
                            "NKN.Tuna",
                            $"event=tuna_acceleration_activation_offer_recovery_joined_retired_generation; trigger={SanitizeLogToken(reason)}; session_id={SanitizeLogToken(sessionId)}; payer_decision_id={payerDecisionId}; generation={offerGeneration}; recovery_reason={SanitizeLogToken(effectiveRecoveryReason)}");
                    }
                }
                else
                {
                    var retryScheduled = ScheduleAccelerationNegotiationRetry(retryReason);
                    if (!retryScheduled)
                    {
                        LocalOperationalLog.Warn(
                            "NKN.Tuna",
                            $"event=tuna_acceleration_activation_offer_retry_not_scheduled; trigger={SanitizeLogToken(reason)}; session_id={SanitizeLogToken(sessionId)}; payer_decision_id={payerDecisionId}; generation={offerGeneration}; retry_reason={SanitizeLogToken(retryReason)}");
                    }
                }
            }
            else
            {
                ClearOutboundAccelerationOfferIfCurrent(nonce, payerDecisionId, offerGeneration, "offer_send_not_observed");
                NotifyTransportAccelerationStateChanged("offer_queue_rejected");
                ResumeActivationPauseIfNeeded("offer_queue_rejected", "offer_queue_rejected");
                ScheduleAccelerationNegotiationRetry("offer_queue_rejected");
            }

            return;
        }

        if (!isRuntimeUnlockActivation)
        {
            PauseActivationIfNeeded("offer_sent");
        }

        NotifyTransportAccelerationStateChanged("waiting_for_answer");
        ScheduleAccelerationOfferReplay(
            remoteEndpoint,
            envelope,
            sessionId,
            nonce,
            payerDecisionId,
            offerGeneration,
            pauseOnQueueAccepted: !isRuntimeUnlockActivation,
            pauseAfterObservedSendOnly: false);
        MarkRuntimeUnlockOfferAnswerTimeoutScheduledIfCurrent(nonce, payerDecisionId, offerGeneration);
        ScheduleAccelerationOfferAnswerTimeout(nonce);
        ScheduleRuntimeUnlockOfferPeerResponseTimeout(
            nonce,
            payerDecisionId,
            offerGeneration,
            sessionId,
            offerSend.ObservedLane);
    }

    private void ScheduleAccelerationOfferReplay(
        string target,
        Envelope envelope,
        string sessionId,
        string nonce,
        long payerDecisionId,
        long generation,
        bool pauseOnQueueAccepted,
        bool pauseAfterObservedSendOnly)
    {
        _ = Task.Run(
            async () =>
            {
                var delay = AccelerationOfferReplayDelayOverrideForTests ?? AccelerationOfferReplayDelay;
                var observedReplaySent = false;
                for (var attempt = 1; attempt <= AccelerationOfferReplayAttempts; attempt++)
                {
                    await Task.Delay(delay, CancellationToken.None).ConfigureAwait(false);
                    if (!IsOutboundAccelerationOfferPending(nonce, payerDecisionId, generation))
                    {
                        return;
                    }

                    var pausedForReplayAttempt = false;
                    void PauseReplayAttempt(string trigger)
                    {
                        pausedForReplayAttempt = PauseFileTransferDataSessionsForTunaActivationNegotiation(
                            "activation_negotiation_pending",
                            sessionId,
                            trigger);
                    }

                    var queued = await SendAccelerationControlEnvelopeWithBulkBypassAsync(
                            target,
                            envelope,
                            "offer_replay",
                        CancellationToken.None,
                        requireObservedSend: true,
                        activationSessionId: sessionId,
                        onQueueAccepted: () =>
                        {
                            if (pauseOnQueueAccepted && ShouldPauseRuntimeUnlockOfferOnQueueAccepted())
                            {
                                PauseReplayAttempt("offer_replay_queue_accepted");
                            }
                        },
                        onObservedSendWaitStarted: pauseAfterObservedSendOnly
                            ? () => PauseReplayAttempt("offer_replay_send_observed")
                            : null,
                        queueAcceptedAsObservedReason: GetRuntimeUnlockOfferQueueAcceptedObservedReason)
                        .ConfigureAwait(false);
                    if (queued.Succeeded)
                    {
                        MarkRuntimeUnlockOfferObservedSendIfCurrent(nonce, payerDecisionId, generation, queued.ObservedLane);
                    }

                    if (pauseAfterObservedSendOnly && queued.Succeeded)
                    {
                        observedReplaySent = true;
                        PauseReplayAttempt("offer_replay_sent");
                    }
                    else if (!queued.Succeeded && (pausedForReplayAttempt || (pauseAfterObservedSendOnly && !observedReplaySent)))
                    {
                        ResumeFileTransferDataSessionsAfterTunaActivationFailure(
                            "offer_replay_not_observed",
                            sessionId,
                            "offer_replay_not_observed");
                    }

                    LocalOperationalLog.Info(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_offer_replay_{(queued.Succeeded ? "sent" : "rejected")}; attempt={attempt}; session_id={SanitizeLogToken(sessionId)}; payer_decision_id={payerDecisionId}; generation={generation}; observed_lane={SanitizeLogToken(queued.ObservedLane)}");
                }
            },
            CancellationToken.None);
    }

    private bool IsOutboundAccelerationOfferPending(string nonce, long payerDecisionId, long generation)
    {
        lock (accelerationGate)
        {
            return outboundAccelerationOfferGeneration == generation &&
                   outboundAccelerationOfferPayerDecisionId == payerDecisionId &&
                   string.Equals(outboundAccelerationOfferNonce, nonce, StringComparison.Ordinal);
        }
    }

    private bool TrySupersedeStaleActiveNegotiationForRuntimeUnlockRecoveryContract(string reason)
    {
        if (!IsRuntimeUnlockActivationReason(reason))
        {
            return false;
        }

        RuntimeUnlockRecoveryRetryState? stateSnapshot = null;
        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            if (state is null ||
                state.RetryObserved ||
                state.ContractState is SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed ||
                !state.RetryQueued ||
                !state.QueuedBehindActiveNegotiation)
            {
                return false;
            }

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var staleWindow = RuntimeUnlockRecoveryContractStaleNegotiationWindowOverrideForTests ??
                RuntimeUnlockRecoveryContractStaleNegotiationWindow;
            if (nowMs - state.CreatedUtcMs < (long)staleWindow.TotalMilliseconds)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(outboundAccelerationOfferNonce) ||
                !string.IsNullOrWhiteSpace(pendingAccelerationAnswerAckNonce))
            {
                return false;
            }

            if (runtimeUnlockOfferProofState is { Retired: false, ObservedSend: true })
            {
                return false;
            }

            state.ContractState = SessionRecoveryContractState.RetryDispatching;
            state.RetryDispatching = true;
            stateSnapshot = state;
        }

        Interlocked.Exchange(ref pendingRuntimeUnlockAccelerationNegotiation, 0);
        Interlocked.Exchange(ref accelerationNegotiationScheduled, 0);
        LogRuntimeUnlockRecoveryContract(
            "session_recovery_contract_stale_negotiation_superseded",
            stateSnapshot!.SessionId);
        return true;
    }

    private void MarkRuntimeUnlockRecoveryContractQueuedBehindActiveNegotiation(string reason)
    {
        RuntimeUnlockRecoveryRetryState? stateSnapshot = null;
        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            if (state is null ||
                state.RetryObserved ||
                state.ContractState is SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed ||
                !IsRuntimeUnlockActivationRetryReason(state.RetryReason))
            {
                return;
            }

            state.QueuedBehindActiveNegotiation = true;
            state.RetryQueued = true;
            state.ContractState = SessionRecoveryContractState.RetryQueued;
            stateSnapshot = state;
        }

        LogRuntimeUnlockRecoveryContract("session_recovery_contract_retry_queued", stateSnapshot!.SessionId);
    }

    private void GrantRuntimeUnlockRecoveryContractRetryAuthorityUnsafe(RuntimeUnlockRecoveryRetryState state)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        RefreshRuntimeUnlockRecoveryContractOfferSendWindowUnsafe(state, nowMs);
        state.AuthorityAttempt++;
    }

    private void RefreshRuntimeUnlockRecoveryContractOfferSendWindowUnsafe(
        RuntimeUnlockRecoveryRetryState state,
        long nowMs)
    {
        var deadline = RuntimeUnlockRetryAuthorityDeadlineOverrideForTests ?? RuntimeUnlockRetryAuthorityDeadline;
        state.RetryAuthorityPending = true;
        state.RetryAuthorityGranted = true;
        state.ObservedSendPending = false;
        state.ObservedSendDeadlineUtcMs = nowMs + (long)deadline.TotalMilliseconds;
        state.RetryDeadlineUtcMs = Math.Max(
            state.RetryDeadlineUtcMs,
            nowMs + (long)RuntimeUnlockRecoveryContractRetryDeadline.TotalMilliseconds);
        state.LivenessDeferralDeadlineUtcMs = Math.Max(
            state.LivenessDeferralDeadlineUtcMs,
            nowMs + (long)RuntimeUnlockRecoveryContractLivenessDeferral.TotalMilliseconds);
        state.AuthorizedObservedLane = null;
        if (!string.Equals(state.AuthorityFailureReason, RuntimeUnlockPostTunaFallbackFinalProbeReason, StringComparison.Ordinal) &&
            !string.Equals(state.AuthorityFailureReason, RuntimeUnlockRegularV4AuthorityProbeReason, StringComparison.Ordinal))
        {
            state.AuthorityFailureReason = null;
        }
    }

    private bool IsRuntimeUnlockRetryAuthorityActiveUnsafe(RuntimeUnlockRecoveryRetryState state, long nowMs)
        => state.RetryAuthorityGranted &&
           state.RetryAuthorityPending &&
           state.ContractState is not (SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed) &&
           !ShouldExpireRuntimeUnlockRetryAuthorityUnsafe(state, nowMs);

    private static bool ShouldExpireRuntimeUnlockRetryAuthorityUnsafe(
        RuntimeUnlockRecoveryRetryState state,
        long nowMs)
    {
        if (!state.RetryAuthorityGranted ||
            !state.RetryAuthorityPending ||
            state.ObservedSendDeadlineUtcMs <= 0 ||
            nowMs <= state.ObservedSendDeadlineUtcMs)
        {
            return false;
        }

        if (state.ObservedSendPending &&
            nowMs <= state.ObservedSendDeadlineUtcMs + (long)RuntimeUnlockRetryAuthorityInFlightSendGrace.TotalMilliseconds)
        {
            return false;
        }

        if ((state.RequiresLocalListenerRetry || state.CutThroughPending) &&
            !state.ListenerRearmCompleted &&
            !state.CutThroughActive &&
            !state.ObservedSendPending)
        {
            return false;
        }

        return !(state.RequiresLocalListenerRetry || state.CutThroughPending) ||
               state.ListenerRearmCompleted ||
               state.ObservedSendPending ||
               state.CurrentOfferGeneration > state.RetiredOfferGeneration;
    }

    private bool RequiresLocalListenerRetryForRuntimeUnlockRecoveryContract(string? sessionId, string reason)
    {
        if (!IsRuntimeUnlockActivationReason(reason) ||
            string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            return state is not null &&
                   state.RequiresLocalListenerRetry &&
                   !state.ListenerRearmCompleted &&
                   (IsSessionRecoveryContractRetryRequired(state) ||
                    state.ContractState == SessionRecoveryContractState.RetryDispatched ||
                    state.RetryAuthorityPending) &&
                   state.ContractState is not (SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed) &&
                   string.Equals(state.SessionId, sessionId.Trim(), StringComparison.Ordinal);
        }
    }

    private bool HasRuntimeUnlockRecoveryContractPendingCutThrough(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            return state is not null &&
                   state.CutThroughPending &&
                   !state.CutThroughActive &&
                   !state.RetryObserved &&
                   state.ContractState is not (SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed) &&
                   string.Equals(state.SessionId, sessionId.Trim(), StringComparison.Ordinal);
        }
    }

    private bool TryGetRuntimeUnlockRetryAuthorityForCurrentOffer(
        out RuntimeUnlockRecoveryRetryState? stateSnapshot,
        string? sessionIdOverride = null)
    {
        stateSnapshot = null;
        var sessionId = string.IsNullOrWhiteSpace(sessionIdOverride)
            ? currentSessionSecurityState.SessionId?.Value
            : sessionIdOverride;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var shouldLogFailure = false;
        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            if (state is null ||
                state.RetryObserved ||
                !string.Equals(state.SessionId, sessionId.Trim(), StringComparison.Ordinal))
            {
                return false;
            }

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (IsRuntimeUnlockRetryAuthorityActiveUnsafe(state, nowMs))
            {
                stateSnapshot = state;
                return true;
            }

            if (ShouldExpireRuntimeUnlockRetryAuthorityUnsafe(state, nowMs))
            {
                state.RetryAuthorityPending = false;
                state.ObservedSendPending = false;
                state.AuthorityFailureReason = "runtime_unlock_retry_authority_expired";
                state.ContractState = SessionRecoveryContractState.Failed;
                stateSnapshot = state;
                shouldLogFailure = true;
            }
        }

        if (shouldLogFailure && stateSnapshot is not null)
        {
            LogRuntimeUnlockRecoveryContract("session_recovery_contract_retry_authority_failed", stateSnapshot.SessionId);
        }

        return false;
    }

    private bool TryGetRuntimeUnlockObservedOfferReplayWindowForCurrentOffer(
        out RuntimeUnlockRecoveryRetryState? stateSnapshot,
        out RuntimeUnlockOfferProofState? offerSnapshot)
    {
        stateSnapshot = null;
        offerSnapshot = null;
        var sessionId = currentSessionSecurityState.SessionId?.Value;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            var offer = runtimeUnlockOfferProofState;
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (state is null ||
                offer is null ||
                state.ContractState is SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed ||
                !string.Equals(state.SessionId, sessionId.Trim(), StringComparison.Ordinal) ||
                !state.RetryDispatched ||
                !state.RetryAuthorityGranted ||
                state.RetryAuthorityPending ||
                !state.ObservedSendPending ||
                state.ObservedSendDeadlineUtcMs <= 0 ||
                nowMs > state.ObservedSendDeadlineUtcMs ||
                offer.Retired ||
                !offer.ObservedSend ||
                offer.PeerReceived ||
                !offer.AnswerTimeoutScheduled ||
                outboundAccelerationOfferGeneration != offer.Generation ||
                outboundAccelerationOfferPayerDecisionId != offer.PayerDecisionId ||
                !string.Equals(outboundAccelerationOfferNonce, offer.Nonce, StringComparison.Ordinal) ||
                state.CurrentOfferGeneration != offer.Generation)
            {
                return false;
            }

            stateSnapshot = state;
            offerSnapshot = offer;
            return true;
        }
    }

    private bool TryGrantRuntimeUnlockRecoveryContractRetryAuthorityForReceiveStallBypass(
        string? sessionId,
        string purpose,
        string? blockerReason)
    {
        if (!IsTunaActivationOfferSendPurpose(purpose) ||
            string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        RuntimeUnlockRecoveryRetryState? stateSnapshot = null;
        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            if (state is null ||
                state.RetryObserved ||
                state.RetryAuthorityGranted ||
                state.ContractState is SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed ||
                !string.Equals(state.SessionId, sessionId.Trim(), StringComparison.Ordinal))
            {
                return false;
            }

            GrantRuntimeUnlockRecoveryContractRetryAuthorityUnsafe(state);
            if (state.RetryDispatched)
            {
                state.ContractState = SessionRecoveryContractState.RetryDispatched;
            }
            else if (state.RetryQueued)
            {
                state.ContractState = SessionRecoveryContractState.RetryQueued;
            }

            stateSnapshot = state;
        }

        LogRuntimeUnlockRecoveryContract("session_recovery_contract_retry_authority_granted", stateSnapshot!.SessionId);
        return true;
    }

    private void MarkRuntimeUnlockRecoveryContractAuthoritySendStarted(string purpose)
    {
        if (!IsTunaActivationOfferSendPurpose(purpose))
        {
            return;
        }

        var isOfferReplay = string.Equals(purpose, "offer_replay", StringComparison.OrdinalIgnoreCase);
        RuntimeUnlockRecoveryRetryState? stateSnapshot = null;
        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            if (state is null ||
                !state.RetryAuthorityGranted ||
                (!state.RetryAuthorityPending && !isOfferReplay) ||
                state.ContractState is SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed)
            {
                return;
            }

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            RefreshRuntimeUnlockRecoveryContractOfferSendWindowUnsafe(state, nowMs);
            state.ObservedSendPending = true;
            stateSnapshot = state;
        }

        LogRuntimeUnlockRecoveryContract("session_recovery_contract_retry_authority_send_started", stateSnapshot!.SessionId);
    }

    private bool TryStartRuntimeUnlockCutThroughForCurrentOffer(
        string sessionId,
        long offerGeneration,
        string trigger)
    {
        if (string.IsNullOrWhiteSpace(sessionId) ||
            offerGeneration <= 0)
        {
            return false;
        }

        RuntimeUnlockRecoveryRetryState? stateSnapshot = null;
        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            if (state is null ||
                state.ContractState is SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed ||
                state.RetryObserved ||
                !state.CutThroughPending ||
                !string.Equals(state.SessionId, sessionId.Trim(), StringComparison.Ordinal))
            {
                return false;
            }

            state.CutThroughPending = false;
            state.CutThroughActive = true;
            state.CutThroughOfferSent = false;
            state.CutThroughPeerReceived = false;
            state.CutThroughCompleted = false;
            state.CutThroughAttempt++;
            state.CurrentOfferGeneration = offerGeneration;
            state.AuthorityFailureReason = null;
            stateSnapshot = state;
        }

        LocalOperationalLog.Warn(
            "NKN.Tuna",
            $"event=runtime_unlock_cutthrough_started; session_id={SanitizeLogToken(sessionId)}; offer_generation={offerGeneration}; contract_generation={stateSnapshot!.ContractGeneration}; attempt={stateSnapshot.CutThroughAttempt}; trigger={SanitizeLogToken(trigger)}; reason=peer_response_timeout_under_regular_v4_recovery");
        LogRuntimeUnlockRecoveryContract("session_recovery_contract_cutthrough_started", stateSnapshot.SessionId);
        return true;
    }

    private void MarkRuntimeUnlockCutThroughOfferSent(
        string sessionId,
        long offerGeneration,
        string? observedLane)
    {
        RuntimeUnlockRecoveryRetryState? stateSnapshot = null;
        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            if (state is null ||
                state.ContractState is SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed ||
                !state.CutThroughActive ||
                !string.Equals(state.SessionId, sessionId.Trim(), StringComparison.Ordinal) ||
                state.CurrentOfferGeneration != offerGeneration)
            {
                return;
            }

            state.CutThroughOfferSent = true;
            state.AuthorizedObservedLane = SanitizeLogToken(observedLane ?? "(none)");
            stateSnapshot = state;
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=runtime_unlock_cutthrough_offer_sent; session_id={SanitizeLogToken(sessionId)}; offer_generation={offerGeneration}; contract_generation={stateSnapshot!.ContractGeneration}; attempt={stateSnapshot.CutThroughAttempt}; observed_lane={SanitizeLogToken(observedLane ?? "(none)")}");
        LogRuntimeUnlockRecoveryContract("session_recovery_contract_cutthrough_offer_sent", stateSnapshot.SessionId);
    }

    private bool MarkRuntimeUnlockCutThroughPeerReceived(
        string sessionId,
        long payerDecisionId,
        string nonce,
        string source)
    {
        RuntimeUnlockRecoveryRetryState? stateSnapshot = null;
        RuntimeUnlockTransactionDecision? transactionDecision = null;
        lock (accelerationGate)
        {
            var offer = runtimeUnlockOfferProofState;
            var state = runtimeUnlockRecoveryRetryState;
            if (offer is null ||
                state is null ||
                state.ContractState is SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed ||
                !state.CutThroughActive ||
                offer.Retired ||
                offer.PayerDecisionId != payerDecisionId ||
                !string.Equals(offer.Nonce, nonce, StringComparison.Ordinal) ||
                !string.Equals(offer.SessionId, sessionId.Trim(), StringComparison.Ordinal) ||
                !string.Equals(state.SessionId, sessionId.Trim(), StringComparison.Ordinal) ||
                state.CurrentOfferGeneration != offer.Generation)
            {
                return false;
            }

            offer.PeerReceived = true;
            state.CutThroughPeerReceived = true;
            state.AuthorityFailureReason = null;
            transactionDecision = ApplyRuntimeUnlockTransactionUnsafe(
                RuntimeUnlockTransactionEventKind.PeerReceived,
                sessionId,
                offer.Generation,
                source);
            stateSnapshot = state;
        }

        LogRuntimeUnlockTransactionDecision(RuntimeUnlockTransactionEventKind.PeerReceived, transactionDecision);
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=runtime_unlock_cutthrough_peer_received; session_id={SanitizeLogToken(sessionId)}; payer_decision_id={payerDecisionId}; offer_generation={stateSnapshot!.CurrentOfferGeneration}; contract_generation={stateSnapshot.ContractGeneration}; attempt={stateSnapshot.CutThroughAttempt}; source={SanitizeLogToken(source)}");
        LogRuntimeUnlockRecoveryContract("session_recovery_contract_cutthrough_peer_received", stateSnapshot.SessionId);
        return true;
    }

    private bool MarkRuntimeUnlockCutThroughAnswerReceived(
        string sessionId,
        long payerDecisionId,
        long offerGeneration,
        string source)
    {
        RuntimeUnlockRecoveryRetryState? stateSnapshot = null;
        RuntimeUnlockTransactionDecision? transactionDecision = null;
        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            if (state is null ||
                state.ContractState is SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed ||
                !state.CutThroughActive ||
                !string.Equals(state.SessionId, sessionId.Trim(), StringComparison.Ordinal) ||
                state.CurrentOfferGeneration != offerGeneration)
            {
                return false;
            }

            state.CutThroughPeerReceived = true;
            state.AuthorityFailureReason = null;
            transactionDecision = ApplyRuntimeUnlockTransactionUnsafe(
                RuntimeUnlockTransactionEventKind.AnswerReceived,
                sessionId,
                offerGeneration,
                source);
            stateSnapshot = state;
        }

        LogRuntimeUnlockTransactionDecision(RuntimeUnlockTransactionEventKind.AnswerReceived, transactionDecision);
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=runtime_unlock_cutthrough_answer_received; session_id={SanitizeLogToken(sessionId)}; payer_decision_id={payerDecisionId}; offer_generation={offerGeneration}; contract_generation={stateSnapshot!.ContractGeneration}; attempt={stateSnapshot.CutThroughAttempt}; source={SanitizeLogToken(source)}");
        LogRuntimeUnlockRecoveryContract("session_recovery_contract_cutthrough_answer_received", stateSnapshot.SessionId);
        return true;
    }

    private void MarkRuntimeUnlockPeerReceivedIfCurrent(
        string sessionId,
        long payerDecisionId,
        string nonce,
        long offerGeneration,
        string source)
    {
        RuntimeUnlockTransactionDecision? transactionDecision = null;
        lock (accelerationGate)
        {
            var offer = runtimeUnlockOfferProofState;
            if (offer is null ||
                offer.Retired ||
                offer.Generation != offerGeneration ||
                offer.PayerDecisionId != payerDecisionId ||
                !string.Equals(offer.Nonce, nonce, StringComparison.Ordinal) ||
                !string.Equals(offer.SessionId, sessionId.Trim(), StringComparison.Ordinal))
            {
                return;
            }

            offer.PeerReceived = true;
            transactionDecision = ApplyRuntimeUnlockTransactionUnsafe(
                RuntimeUnlockTransactionEventKind.PeerReceived,
                sessionId,
                offer.Generation,
                source);
        }

        LogRuntimeUnlockTransactionDecision(RuntimeUnlockTransactionEventKind.PeerReceived, transactionDecision);
    }

    private void MarkRuntimeUnlockAnswerReceivedIfCurrent(
        string sessionId,
        long payerDecisionId,
        string nonce,
        long offerGeneration,
        string source)
    {
        RuntimeUnlockTransactionDecision? transactionDecision = null;
        lock (accelerationGate)
        {
            var offer = runtimeUnlockOfferProofState;
            if (offer is null ||
                offer.Retired ||
                offer.Generation != offerGeneration ||
                offer.PayerDecisionId != payerDecisionId ||
                !string.Equals(offer.Nonce, nonce, StringComparison.Ordinal) ||
                !string.Equals(offer.SessionId, sessionId.Trim(), StringComparison.Ordinal))
            {
                return;
            }

            offer.PeerReceived = true;
            transactionDecision = ApplyRuntimeUnlockTransactionUnsafe(
                RuntimeUnlockTransactionEventKind.AnswerReceived,
                sessionId,
                offer.Generation,
                source);
        }

        LogRuntimeUnlockTransactionDecision(RuntimeUnlockTransactionEventKind.AnswerReceived, transactionDecision);
    }

    private void MarkInboundRuntimeUnlockOfferAccepted(
        TransportAccelerationOfferPayload offer,
        long answerAckGeneration)
    {
        if (answerAckGeneration <= 0 ||
            !IsRuntimeUnlockActivationReason(offer.Trigger))
        {
            return;
        }

        var sessionId = string.IsNullOrWhiteSpace(offer.SessionId)
            ? currentSessionSecurityState.SessionId?.Value ?? string.Empty
            : offer.SessionId.Trim();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        RuntimeUnlockTransactionDecision? started;
        RuntimeUnlockTransactionDecision? peerReceived;
        RuntimeUnlockTransactionDecision? answerReceived;
        lock (accelerationGate)
        {
            started = StartRuntimeUnlockTransactionForOfferUnsafe(
                sessionId,
                answerAckGeneration,
                "peer_runtime_unlock_offer_accepted");
            peerReceived = ApplyRuntimeUnlockTransactionUnsafe(
                RuntimeUnlockTransactionEventKind.PeerReceived,
                sessionId,
                answerAckGeneration,
                "transport_acceleration_offer_received");
            answerReceived = ApplyRuntimeUnlockTransactionUnsafe(
                RuntimeUnlockTransactionEventKind.AnswerReceived,
                sessionId,
                answerAckGeneration,
                "transport_acceleration_answer_pending");
        }

        LogRuntimeUnlockTransactionDecision(RuntimeUnlockTransactionEventKind.OfferGenerationCreated, started);
        LogRuntimeUnlockTransactionDecision(RuntimeUnlockTransactionEventKind.PeerReceived, peerReceived);
        LogRuntimeUnlockTransactionDecision(RuntimeUnlockTransactionEventKind.AnswerReceived, answerReceived);
    }

    private bool FailRuntimeUnlockCutThroughIfCurrent(
        string sessionId,
        long payerDecisionId,
        long offerGeneration,
        string reason)
    {
        RuntimeUnlockRecoveryRetryState? stateSnapshot = null;
        RuntimeUnlockTransactionDecision? transactionDecision = null;
        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            if (state is null ||
                state.ContractState is SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed ||
                !state.CutThroughActive ||
                state.CutThroughPeerReceived ||
                !string.Equals(state.SessionId, sessionId.Trim(), StringComparison.Ordinal) ||
                state.CurrentOfferGeneration != offerGeneration)
            {
                return false;
            }

            state.RetryAuthorityPending = false;
            state.ObservedSendPending = false;
            state.AuthorityFailureReason = SanitizeLogToken(reason);
            state.ContractState = SessionRecoveryContractState.Failed;
            transactionDecision = ApplyRuntimeUnlockTransactionUnsafe(
                RuntimeUnlockTransactionEventKind.Failed,
                sessionId,
                offerGeneration,
                reason);
            stateSnapshot = state;
        }

        LogRuntimeUnlockTransactionDecision(RuntimeUnlockTransactionEventKind.Failed, transactionDecision);
        LocalOperationalLog.Warn(
            "NKN.Tuna",
            $"event=runtime_unlock_cutthrough_failed; session_id={SanitizeLogToken(sessionId)}; payer_decision_id={payerDecisionId}; offer_generation={offerGeneration}; contract_generation={stateSnapshot!.ContractGeneration}; attempt={stateSnapshot.CutThroughAttempt}; reason={SanitizeLogToken(reason)}");
        LogRuntimeUnlockRecoveryContract("session_recovery_contract_failed", stateSnapshot.SessionId);
        return true;
    }

    private void MarkRuntimeUnlockRecoveryContractAuthorityBlocked(string? reason)
    {
        RuntimeUnlockRecoveryRetryState? stateSnapshot = null;
        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            if (state is null ||
                !state.RetryAuthorityGranted ||
                state.ContractState is SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed)
            {
                return;
            }

            state.AuthorityFailureReason = string.IsNullOrWhiteSpace(reason)
                ? "runtime_unlock_retry_authority_offer_blocked"
                : SanitizeLogToken(reason);
            stateSnapshot = state;
        }

        LogRuntimeUnlockRecoveryContract("session_recovery_contract_retry_authority_send_blocked", stateSnapshot!.SessionId);
    }

    private bool ShouldPromoteRuntimeUnlockAuthoritySendBlockToCutThrough(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var normalizedSessionId = sessionId.Trim();
        if (!HasActiveRegularV4FileTransferRouteHint(normalizedSessionId) ||
            HasActivePostTunaFallbackFileTransferRouteHint(normalizedSessionId) ||
            TryGetUnresolvedFileTransferV6TransportEpochForCurrentSession(out _))
        {
            return false;
        }

        if (!TryGetRuntimeUnlockRetryAuthorityForCurrentOffer(out var state, normalizedSessionId) ||
            state is null ||
            !state.RetryAuthorityGranted ||
            state.RetryObserved ||
            state.CutThroughActive ||
            state.ContractState is SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed)
        {
            return false;
        }

        var failureReason = SanitizeLogToken(state.AuthorityFailureReason);
        return failureReason is "runtime_unlock_retry_authority_offer_blocked" or
            "regular_v4_receive_recovery_unproven" or
            "runtime_unlock_authority_missing_recent_peer_proof";
    }

    private bool TryDeferRuntimeUnlockForRegularV4ReceiveRecovery(
        string? sessionId,
        string trigger,
        long payerDecisionId,
        long offerGeneration,
        out string blockerReason,
        out long blockerRemainingMs)
    {
        if (!TryGetRuntimeUnlockRegularV4ReceiveRecoveryBlocker(out blockerReason, out blockerRemainingMs))
        {
            return false;
        }

        if (ShouldBypassRuntimeUnlockRegularV4DispatchBlockerAfterReceiveProof(
                sessionId,
                blockerReason,
                out var proofReason))
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_runtime_unlock_regular_v4_receive_recovery_blocker_bypassed; session_id={SanitizeLogToken(sessionId ?? "none")}; trigger={SanitizeLogToken(trigger)}; payer_decision_id={payerDecisionId}; generation={offerGeneration}; blocker_reason={SanitizeLogToken(blockerReason)}; proof_reason={SanitizeLogToken(proofReason)}; reason=validated_regular_v4_receive_proof");
            return false;
        }

        if (string.Equals(trigger, "peer_response_timeout_without_peer_response", StringComparison.OrdinalIgnoreCase))
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_acceleration_runtime_unlock_regular_v4_receive_recovery_requires_fresh_peer_response_retry; session_id={SanitizeLogToken(sessionId ?? "none")}; trigger={SanitizeLogToken(trigger)}; payer_decision_id={payerDecisionId}; generation={offerGeneration}; blocker_reason={SanitizeLogToken(blockerReason)}; blocker_remaining_ms={blockerRemainingMs}; reason=observed_offer_without_peer_response");
            return false;
        }

        MarkRuntimeUnlockRecoveryContractAuthorityBlocked("regular_v4_receive_recovery_unproven");
        FailRuntimeUnlockRecoveryContractIfActive(sessionId, "runtime_unlock_deferred_regular_v4_receive_recovery");
        LocalOperationalLog.Warn(
            "NKN.Tuna",
            $"event=tuna_acceleration_runtime_unlock_deferred_for_regular_v4_receive_recovery; session_id={SanitizeLogToken(sessionId ?? "none")}; trigger={SanitizeLogToken(trigger)}; payer_decision_id={payerDecisionId}; generation={offerGeneration}; blocker_reason={SanitizeLogToken(blockerReason)}; blocker_remaining_ms={blockerRemainingMs}; retry_scheduled=0; recovery_requested=0");
        return true;
    }

    private bool TryDeferRuntimeUnlockOfferDispatchForRegularV4ReceiveRecovery(
        string? sessionId,
        string trigger,
        long payerDecisionId,
        out string blockerReason,
        out long blockerRemainingMs)
    {
        if (!TryGetRuntimeUnlockRegularV4ReceiveRecoveryBlocker(out blockerReason, out blockerRemainingMs))
        {
            return false;
        }

        if (ShouldBypassRuntimeUnlockRegularV4DispatchBlockerAfterReceiveProof(
                sessionId,
                blockerReason,
                out var proofReason))
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_runtime_unlock_dispatch_regular_v4_receive_recovery_bypassed; session_id={SanitizeLogToken(sessionId ?? "none")}; trigger={SanitizeLogToken(trigger)}; payer_decision_id={payerDecisionId}; blocker_reason={SanitizeLogToken(blockerReason)}; proof_reason={SanitizeLogToken(proofReason)}; reason=validated_regular_v4_receive_proof");
            return false;
        }

        if (ShouldBypassRuntimeUnlockRegularV4DispatchBlockerForRetryAuthority(
                sessionId,
                blockerReason,
                out var authorityState))
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_acceleration_runtime_unlock_dispatch_regular_v4_receive_recovery_authority_bypassed; session_id={SanitizeLogToken(sessionId ?? "none")}; trigger={SanitizeLogToken(trigger)}; payer_decision_id={payerDecisionId}; blocker_reason={SanitizeLogToken(blockerReason)}; blocker_remaining_ms={blockerRemainingMs}; contract_generation={authorityState!.ContractGeneration}; authority_attempt={authorityState.AuthorityAttempt}; reason=bounded_authority_observed_send_probe");
            return false;
        }

        if (ShouldBypassRuntimeUnlockRegularV4DispatchBlockerForPostTunaFallbackAuthority(
                sessionId,
                blockerReason,
                out authorityState))
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_acceleration_runtime_unlock_dispatch_regular_v4_receive_recovery_post_fallback_authority_bypassed; session_id={SanitizeLogToken(sessionId ?? "none")}; trigger={SanitizeLogToken(trigger)}; payer_decision_id={payerDecisionId}; blocker_reason={SanitizeLogToken(blockerReason)}; blocker_remaining_ms={blockerRemainingMs}; contract_generation={authorityState!.ContractGeneration}; authority_attempt={authorityState.AuthorityAttempt}; listener_rearm_completed={(authorityState.ListenerRearmCompleted ? 1 : 0)}; reason=post_tuna_fallback_bounded_authority_observed_send_probe");
            return false;
        }

        if (ShouldBypassRuntimeUnlockRegularV4DispatchBlockerForFinalFirstOfferProbe(
                sessionId,
                blockerReason,
                out var retryAttempts,
                out var maxAttempts))
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_acceleration_runtime_unlock_dispatch_regular_v4_receive_recovery_first_offer_probe_allowed; session_id={SanitizeLogToken(sessionId ?? "none")}; trigger={SanitizeLogToken(trigger)}; payer_decision_id={payerDecisionId}; blocker_reason={SanitizeLogToken(blockerReason)}; blocker_remaining_ms={blockerRemainingMs}; retry_attempts={retryAttempts}; max_attempts={maxAttempts}; reason=bounded_final_first_offer_observed_send_probe");
            return false;
        }

        MarkRuntimeUnlockRecoveryContractDispatchDeferredForRegularV4ReceiveRecovery(
            sessionId,
            "regular_v4_receive_recovery_pending");
        NotifyTransportAccelerationStateChanged("runtime_unlock_regular_v4_receive_recovery_pending");
        var retryAfterReceiveProofArmed = false;
        var retryScheduled = false;
        if (TryGetRuntimeUnlockRecoveryContractForSession(sessionId, out var retiredOfferGeneration, out var contractSessionId))
        {
            ScheduleRuntimeUnlockRetryAfterFallbackRepairSoftSettle(
                retiredOfferGeneration,
                contractSessionId);
            retryAfterReceiveProofArmed = true;
        }
        else if (!string.IsNullOrWhiteSpace(sessionId))
        {
            ArmRuntimeUnlockRetryAfterRecovery(
                0,
                sessionId.Trim(),
                "runtime_unlock_regular_v4_receive_recovery_pending",
                "regular_v4_receive_recovery_pending");
            retryAfterReceiveProofArmed = true;
        }
        else
        {
            retryScheduled = ScheduleAccelerationNegotiationRetry(
                "runtime_unlock_regular_v4_receive_recovery_pending");
        }

        LocalOperationalLog.Warn(
            "NKN.Tuna",
            $"event=tuna_acceleration_runtime_unlock_dispatch_deferred_for_regular_v4_receive_recovery; session_id={SanitizeLogToken(sessionId ?? "none")}; trigger={SanitizeLogToken(trigger)}; payer_decision_id={payerDecisionId}; blocker_reason={SanitizeLogToken(blockerReason)}; blocker_remaining_ms={blockerRemainingMs}; retry_scheduled={(retryScheduled ? 1 : 0)}; retry_after_receive_proof_armed={(retryAfterReceiveProofArmed ? 1 : 0)}; recovery_requested=0");
        return true;
    }

    private bool TryGetRuntimeUnlockRegularV4ReceiveRecoveryBlocker(
        out string blockerReason,
        out long blockerRemainingMs)
    {
        blockerReason = string.Empty;
        blockerRemainingMs = 0;
        if (RuntimeUnlockRegularV4ReceiveRecoveryBlockerOverrideForTests?.Invoke(this) is { Length: > 0 } overrideReason)
        {
            blockerReason = SanitizeLogToken(overrideReason);
            return true;
        }

        if (client is RealNknClientAdapter realClient &&
            realClient.TryGetFileTransferRegularV4ActivationSendBlocker(
                out var adapterReason,
                out blockerRemainingMs,
                includeRegularV4Pressure: false) &&
            IsRuntimeUnlockRegularV4ReceiveRecoveryBlocker(adapterReason))
        {
            blockerReason = SanitizeLogToken(adapterReason);
            return true;
        }

        return false;
    }

    private void MarkRuntimeUnlockRecoveryContractDispatchDeferredForRegularV4ReceiveRecovery(
        string? sessionId,
        string reason)
    {
        RuntimeUnlockRecoveryRetryState? stateSnapshot = null;
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? string.Empty : sessionId.Trim();
        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            if (state is null ||
                state.RetryObserved ||
                state.ContractState is SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed ||
                (!string.IsNullOrWhiteSpace(normalizedSessionId) &&
                 !string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal)))
            {
                return;
            }

            if (state.ObservedSendPending ||
                state.CutThroughPending ||
                state.CutThroughActive)
            {
                stateSnapshot = state;
                return;
            }

            state.Settled = true;
            state.ContractState = SessionRecoveryContractState.RecoverySettled;
            state.RetryQueued = false;
            state.RetryDispatching = false;
            state.RetryDispatched = false;
            state.RetryAuthorityPending = false;
            state.RetryAuthorityGranted = false;
            state.ObservedSendPending = false;
            state.ObservedSendDeadlineUtcMs = 0;
            state.AuthorizedObservedLane = null;
            state.AuthorityFailureReason = SanitizeLogToken(reason);
            stateSnapshot = state;
        }

        LogRuntimeUnlockRecoveryContract("session_recovery_contract_recovery_settled", stateSnapshot!.SessionId);
    }

    private bool TryGetRuntimeUnlockRecoveryContractForSession(
        string? sessionId,
        out long retiredOfferGeneration,
        out string contractSessionId)
    {
        retiredOfferGeneration = 0;
        contractSessionId = string.Empty;
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? string.Empty : sessionId.Trim();
        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            if (state is null ||
                (!string.IsNullOrWhiteSpace(normalizedSessionId) &&
                 !string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal)))
            {
                return false;
            }

            retiredOfferGeneration = state.RetiredOfferGeneration;
            contractSessionId = state.SessionId;
            return true;
        }
    }

    private bool ShouldBypassRuntimeUnlockRegularV4DispatchBlockerAfterReceiveProof(
        string? sessionId,
        string? blockerReason,
        out string proofReason)
    {
        proofReason = "none";
        if (string.IsNullOrWhiteSpace(sessionId) ||
            !IsRuntimeUnlockRegularV4ReceiveRecoveryBlocker(blockerReason))
        {
            return false;
        }

        var normalizedSessionId = sessionId.Trim();
        if (!HasActiveRegularV4FileTransferRouteHint(normalizedSessionId))
        {
            return false;
        }

        if (!TryGetActiveRegularV4RecoveryLivenessStatus(
                normalizedSessionId,
                out var receiveProofObserved,
                out var terminal,
                out var deadlineExpired,
                out var stateReason,
                out _))
        {
            return false;
        }

        proofReason = stateReason;
        return receiveProofObserved && !terminal && !deadlineExpired;
    }

    private bool ShouldBypassRuntimeUnlockRegularV4DispatchBlockerForRetryAuthority(
        string? sessionId,
        string? blockerReason,
        out RuntimeUnlockRecoveryRetryState? authorityState)
    {
        authorityState = null;
        if (string.IsNullOrWhiteSpace(sessionId) ||
            !IsRuntimeUnlockRegularV4ReceiveRecoveryBlocker(blockerReason))
        {
            return false;
        }

        var normalizedSessionId = sessionId.Trim();
        if (!HasActiveRegularV4FileTransferRouteHint(normalizedSessionId) ||
            HasActivePostTunaFallbackFileTransferRouteHint(normalizedSessionId) ||
            TryGetUnresolvedFileTransferV6TransportEpochForCurrentSession(out _))
        {
            return false;
        }

        if (!TryGetRuntimeUnlockRetryAuthorityForCurrentOffer(out var state) ||
            state is not { RetryDispatched: true, RetryAuthorityGranted: true, RetryAuthorityPending: true } ||
            !string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal) ||
            (!state.CutThroughPending &&
             !string.Equals(state.AuthorityFailureReason, RuntimeUnlockRegularV4AuthorityProbeReason, StringComparison.Ordinal)))
        {
            return false;
        }

        authorityState = state;
        return true;
    }

    private bool ShouldBypassRuntimeUnlockRegularV4DispatchBlockerForPostTunaFallbackAuthority(
        string? sessionId,
        string? blockerReason,
        out RuntimeUnlockRecoveryRetryState? authorityState)
    {
        authorityState = null;
        if (string.IsNullOrWhiteSpace(sessionId) ||
            !IsRuntimeUnlockRegularV4ReceiveRecoveryBlocker(blockerReason))
        {
            return false;
        }

        var normalizedSessionId = sessionId.Trim();
        if (!HasActivePostTunaFallbackFileTransferRouteHint(normalizedSessionId))
        {
            return false;
        }

        if (!TryGetRuntimeUnlockRetryAuthorityForCurrentOffer(out var state) ||
            state is not { RetryDispatched: true, RetryAuthorityGranted: true, RetryAuthorityPending: true } ||
            !string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal))
        {
            return false;
        }

        if (state.RequiresLocalListenerRetry && !state.ListenerRearmCompleted)
        {
            return false;
        }

        if (state.CutThroughPending ||
            string.Equals(state.AuthorityFailureReason, RuntimeUnlockPostTunaFallbackFinalProbeReason, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(state.AuthorityFailureReason))
        {
            authorityState = state;
            return true;
        }

        return false;
    }

    private bool ShouldBypassRuntimeUnlockRegularV4DispatchBlockerForFinalFirstOfferProbe(
        string? sessionId,
        string? blockerReason,
        out int retryAttempts,
        out int maxAttempts)
    {
        retryAttempts = Volatile.Read(ref accelerationNegotiationRetryAttempts);
        maxAttempts = RuntimeUnlockAccelerationNegotiationMaxRetryAttempts;
        if (string.IsNullOrWhiteSpace(sessionId) ||
            !IsRuntimeUnlockRegularV4ReceiveRecoveryBlocker(blockerReason))
        {
            return false;
        }

        var normalizedSessionId = sessionId.Trim();
        if (!HasActiveRegularV4FileTransferRouteHint(normalizedSessionId) ||
            HasActivePostTunaFallbackFileTransferRouteHint(normalizedSessionId) ||
            TryGetUnresolvedFileTransferV6TransportEpochForCurrentSession(out _))
        {
            return false;
        }

        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            if (state is not null &&
                state.ContractState is not (SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed) &&
                string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return retryAttempts >= Math.Max(1, maxAttempts - 1);
    }

    private static bool IsRuntimeUnlockRegularV4ReceiveRecoveryBlocker(string? reason)
    {
        var normalized = SanitizeLogToken(reason);
        return normalized is "receive_stall_recovery_in_progress" or
            "receive_stall_recovery_awaiting_receive_proof" or
            "all_zero_receive_window" or
            "control_zero_receive_window" or
            "bulk_zero_receive_window" or
            "recent_zero_receive_health";
    }

    private static bool IsRuntimeUnlockRegularV4ReceiveRecoveryPendingAuthorityReason(string? reason)
        => string.Equals(
            SanitizeLogToken(reason),
            "regular_v4_receive_recovery_pending",
            StringComparison.Ordinal);

    private void MarkRuntimeUnlockRecoveryContractRetryDispatched(string reason)
    {
        if (!IsRuntimeUnlockActivationReason(reason))
        {
            return;
        }

        RuntimeUnlockRecoveryRetryState? stateSnapshot = null;
        var authorityGranted = false;
        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            if (state is null ||
                state.RetryObserved ||
                state.ContractState is SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed)
            {
                return;
            }

            state.RetryQueued = true;
            state.RetryDispatching = true;
            state.RetryDispatched = true;
            if (state.Settled && !state.RetryAuthorityGranted)
            {
                GrantRuntimeUnlockRecoveryContractRetryAuthorityUnsafe(state);
                authorityGranted = true;
            }
            state.ContractState = SessionRecoveryContractState.RetryDispatched;
            stateSnapshot = state;
        }

        if (authorityGranted)
        {
            LogRuntimeUnlockRecoveryContract("session_recovery_contract_retry_authority_granted", stateSnapshot!.SessionId);
        }

        LogRuntimeUnlockRecoveryContract("session_recovery_contract_retry_dispatched", stateSnapshot!.SessionId);
    }

    private bool HasPendingOutboundAccelerationOffer()
    {
        lock (accelerationGate)
        {
            return !string.IsNullOrWhiteSpace(outboundAccelerationOfferNonce);
        }
    }

    private bool HasPendingAccelerationAnswerAck()
    {
        lock (accelerationGate)
        {
            return !string.IsNullOrWhiteSpace(pendingAccelerationAnswerAckNonce) &&
                   pendingAccelerationAnswerAckPayerDecisionId > 0;
        }
    }

    private void ClearOutboundAccelerationOfferIfCurrent(
        string nonce,
        long payerDecisionId,
        long generation,
        string reason)
    {
        lock (accelerationGate)
        {
            if (outboundAccelerationOfferGeneration != generation ||
                outboundAccelerationOfferPayerDecisionId != payerDecisionId ||
                !string.Equals(outboundAccelerationOfferNonce, nonce, StringComparison.Ordinal))
            {
                return;
            }

            ClearOutboundAccelerationOfferLocked();
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_outbound_offer_cleared; reason={SanitizeLogToken(reason)}; payer_decision_id={payerDecisionId}; generation={generation}");
    }

    private void MarkRuntimeUnlockOfferObservedSendIfCurrent(
        string nonce,
        long payerDecisionId,
        long generation,
        string? observedLane)
    {
        if (string.IsNullOrWhiteSpace(observedLane))
        {
            return;
        }

        lock (accelerationGate)
        {
            var state = runtimeUnlockOfferProofState;
            if (state is null ||
                state.Retired ||
                state.Generation != generation ||
                state.PayerDecisionId != payerDecisionId ||
                !string.Equals(state.Nonce, nonce, StringComparison.Ordinal))
            {
                return;
            }

            state.ObservedSend = true;
            state.ObservedSendLane = SanitizeLogToken(observedLane);
        }
    }

    private void MarkRuntimeUnlockRecoveryContractRetryObserved(
        string sessionId,
        long offerGeneration,
        string? observedLane)
    {
        RuntimeUnlockRecoveryRetryState? stateSnapshot = null;
        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            if (state is null ||
                !string.Equals(state.SessionId, sessionId.Trim(), StringComparison.Ordinal))
            {
                return;
            }

            if (state.ContractState == SessionRecoveryContractState.Failed)
            {
                return;
            }

            if (state.ContractState == SessionRecoveryContractState.Completed)
            {
                if (state.CurrentOfferGeneration > 0 &&
                    offerGeneration > 0 &&
                    state.CurrentOfferGeneration != offerGeneration)
                {
                    return;
                }

                state.AuthorizedObservedLane = SanitizeLogToken(observedLane ?? "(none)");
                stateSnapshot = state;
            }
            else
            {
                if (state.RetryObserved)
                {
                    return;
                }

                var now = DateTimeOffset.UtcNow;
                var answerTimeout = AccelerationOfferAnswerTimeoutOverrideForTests ??
                    RuntimeUnlockAccelerationOfferAnswerTimeout;
                var peerProofDeadline = now.Add(answerTimeout + TimeSpan.FromSeconds(2)).ToUnixTimeMilliseconds();

                state.RetryDispatched = true;
                state.RetryDispatching = false;
                state.CurrentOfferGeneration = offerGeneration;
                state.RetryAuthorityPending = false;
                state.RetryAuthorityGranted = true;
                state.ObservedSendPending = true;
                state.AuthorizedObservedLane = SanitizeLogToken(observedLane ?? "(none)");
                state.AuthorityFailureReason = null;
                state.ObservedSendDeadlineUtcMs = Math.Max(state.ObservedSendDeadlineUtcMs, peerProofDeadline);
                state.RetryDeadlineUtcMs = Math.Max(state.RetryDeadlineUtcMs, peerProofDeadline);
                state.LivenessDeferralDeadlineUtcMs = Math.Max(state.LivenessDeferralDeadlineUtcMs, peerProofDeadline);
                state.ContractState = SessionRecoveryContractState.RetryDispatched;
                stateSnapshot = state;
            }
        }

        LogRuntimeUnlockRecoveryContract("session_recovery_contract_retry_authority_observed", stateSnapshot!.SessionId);
    }

    private void CompleteRuntimeUnlockRecoveryContractIfActive(string sessionId, string reason)
    {
        RuntimeUnlockRecoveryRetryState? stateSnapshot = null;
        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            if (state is null ||
                state.ContractState is SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed ||
                !string.Equals(state.SessionId, sessionId.Trim(), StringComparison.Ordinal))
            {
                return;
            }

            state.RetryObserved = true;
            state.RetryDispatching = false;
            state.RetryDispatched = true;
            state.RetryAuthorityPending = false;
            state.ObservedSendPending = false;
            ClearRuntimeUnlockBrokenStateRetryBudgetUnsafe(sessionId);
            var cutThroughCompleted = state.CutThroughActive && !state.CutThroughCompleted;
            if (cutThroughCompleted)
            {
                state.CutThroughCompleted = true;
                state.CutThroughPeerReceived = true;
            }

            state.ContractState = SessionRecoveryContractState.Completed;
            stateSnapshot = state;
        }

        if (stateSnapshot!.CutThroughCompleted)
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=runtime_unlock_cutthrough_completed; session_id={SanitizeLogToken(stateSnapshot.SessionId)}; offer_generation={stateSnapshot.CurrentOfferGeneration}; contract_generation={stateSnapshot.ContractGeneration}; attempt={stateSnapshot.CutThroughAttempt}; reason={SanitizeLogToken(reason)}");
        }

        LogRuntimeUnlockRecoveryContract("session_recovery_contract_completed", stateSnapshot!.SessionId);
    }

    private void MarkRuntimeUnlockRecoveryContractListenerRearmCompleted(string sessionId, string reason)
    {
        RuntimeUnlockRecoveryRetryState? stateSnapshot = null;
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? string.Empty : sessionId.Trim();
        if (string.IsNullOrWhiteSpace(normalizedSessionId))
        {
            return;
        }

        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            if (state is null ||
                (!state.RequiresLocalListenerRetry && !state.CutThroughPending && !state.RetryAuthorityPending) ||
                state.ContractState is SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed ||
                !string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal))
            {
                return;
            }

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            RefreshRuntimeUnlockRecoveryContractOfferSendWindowUnsafe(state, nowMs);
            state.AuthorityFailureReason = null;
            state.ListenerRearmCompleted = true;
            state.ObservedSendPending = false;
            state.ContractState = state.RetryDispatched
                ? SessionRecoveryContractState.RetryDispatched
                : SessionRecoveryContractState.RetryQueued;
            stateSnapshot = state;
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=session_recovery_contract_listener_rearm_completed; session_id={SanitizeLogToken(normalizedSessionId)}; trigger={SanitizeLogToken(reason)}");
        LogRuntimeUnlockRecoveryContract("session_recovery_contract_listener_rearm_completed", stateSnapshot!.SessionId);
    }

    private void FailRuntimeUnlockRecoveryContractIfActive(string? sessionId, string reason)
    {
        RuntimeUnlockRecoveryRetryState? stateSnapshot = null;
        RuntimeUnlockTransactionDecision? transactionDecision = null;
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? string.Empty : sessionId.Trim();
        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            if (state is null ||
                state.ContractState is SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed ||
                (!string.IsNullOrWhiteSpace(normalizedSessionId) &&
                 !string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal)))
            {
                return;
            }

            state.RetryDispatching = false;
            state.RetryAuthorityPending = false;
            state.ObservedSendPending = false;
            state.AuthorityFailureReason = SanitizeLogToken(reason);
            state.ContractState = SessionRecoveryContractState.Failed;
            transactionDecision = ApplyRuntimeUnlockTransactionUnsafe(
                RuntimeUnlockTransactionEventKind.Failed,
                state.SessionId,
                state.CurrentOfferGeneration > 0 ? state.CurrentOfferGeneration : state.RetiredOfferGeneration,
                reason);
            stateSnapshot = state;
        }

        LogRuntimeUnlockTransactionDecision(RuntimeUnlockTransactionEventKind.Failed, transactionDecision);
        LogRuntimeUnlockRecoveryContract("session_recovery_contract_failed", stateSnapshot!.SessionId);
    }

    private bool TryConsumeRuntimeUnlockBrokenStateRetryBudget(string normalizedReason)
    {
        if (!IsRuntimeUnlockBrokenStateRetryReason(normalizedReason))
        {
            return true;
        }

        RuntimeUnlockRecoveryRetryState? consumedState = null;
        RuntimeUnlockRecoveryRetryState? suppressedState = null;
        RuntimeUnlockTransactionDecision? suppressedDecision = null;
        var family = GetRuntimeUnlockBrokenStateRetryFamily(normalizedReason);
        var failureReason = $"runtime_unlock_broken_state_retry_exhausted_{family}";
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            if (state is null ||
                state.ContractState is SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed)
            {
                return true;
            }

            var budgetExpired = runtimeUnlockBrokenStateRetryBudgetStartedUtcMs <= 0 ||
                nowMs - runtimeUnlockBrokenStateRetryBudgetStartedUtcMs >
                    (long)RuntimeUnlockBrokenStateRetryBudgetTtl.TotalMilliseconds;
            if (budgetExpired ||
                !string.Equals(runtimeUnlockBrokenStateRetryBudgetSessionId, state.SessionId, StringComparison.Ordinal) ||
                !string.Equals(runtimeUnlockBrokenStateRetryBudgetFamily, family, StringComparison.Ordinal))
            {
                runtimeUnlockBrokenStateRetryBudgetSessionId = state.SessionId;
                runtimeUnlockBrokenStateRetryBudgetFamily = family;
                runtimeUnlockBrokenStateRetryBudgetStartedUtcMs = nowMs;
                runtimeUnlockBrokenStateFreshRetriesUsed = 0;
            }

            if (runtimeUnlockBrokenStateFreshRetriesUsed >= RuntimeUnlockBrokenStateMaxFreshRetries)
            {
                state.RetryQueued = false;
                state.RetryDispatching = false;
                state.RetryAuthorityPending = false;
                state.RetryAuthorityGranted = false;
                state.ObservedSendPending = false;
                state.AuthorityFailureReason = failureReason;
                state.BrokenStateFinalized = true;
                state.BrokenStateFailureReason = failureReason;
                state.ContractState = SessionRecoveryContractState.Failed;
                suppressedDecision = ApplyRuntimeUnlockTransactionUnsafe(
                    RuntimeUnlockTransactionEventKind.Failed,
                    state.SessionId,
                    state.CurrentOfferGeneration > 0 ? state.CurrentOfferGeneration : state.RetiredOfferGeneration,
                    failureReason);
                suppressedState = state;
            }
            else
            {
                runtimeUnlockBrokenStateFreshRetriesUsed++;
                state.BrokenStateFreshRetryAttempt = runtimeUnlockBrokenStateFreshRetriesUsed;
                consumedState = state;
            }
        }

        if (suppressedState is not null)
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=runtime_unlock_broken_state_retry_suppressed; session_id={SanitizeLogToken(suppressedState.SessionId)}; transfer_id={SanitizeLogToken(suppressedState.TransferId ?? "(none)")}; contract_generation={suppressedState.ContractGeneration}; offer_generation={(suppressedState.CurrentOfferGeneration > 0 ? suppressedState.CurrentOfferGeneration : suppressedState.RetiredOfferGeneration)}; reason={SanitizeLogToken(normalizedReason)}; family={SanitizeLogToken(family)}; fresh_retry_attempts={runtimeUnlockBrokenStateFreshRetriesUsed}; max_fresh_retries={RuntimeUnlockBrokenStateMaxFreshRetries}; final_reason={SanitizeLogToken(failureReason)}");
            LogRuntimeUnlockTransactionDecision(RuntimeUnlockTransactionEventKind.Failed, suppressedDecision);
            LogRuntimeUnlockRecoveryContract("session_recovery_contract_failed", suppressedState.SessionId);
            TryResumeFileTransferDataSessionsAfterTunaActivationNegotiation(
                "runtime_unlock_broken_state_retry_exhausted",
                suppressedState.SessionId,
                failureReason);
            return false;
        }

        if (consumedState is not null)
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=runtime_unlock_broken_state_fresh_retry_consumed; session_id={SanitizeLogToken(consumedState.SessionId)}; transfer_id={SanitizeLogToken(consumedState.TransferId ?? "(none)")}; contract_generation={consumedState.ContractGeneration}; offer_generation={(consumedState.CurrentOfferGeneration > 0 ? consumedState.CurrentOfferGeneration : consumedState.RetiredOfferGeneration)}; reason={SanitizeLogToken(normalizedReason)}; family={SanitizeLogToken(family)}; fresh_retry_attempt={consumedState.BrokenStateFreshRetryAttempt}; max_fresh_retries={RuntimeUnlockBrokenStateMaxFreshRetries}");
        }

        return true;
    }

    private void ClearRuntimeUnlockBrokenStateRetryBudgetUnsafe(string? sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId) &&
            !string.IsNullOrWhiteSpace(runtimeUnlockBrokenStateRetryBudgetSessionId) &&
            !string.Equals(runtimeUnlockBrokenStateRetryBudgetSessionId, sessionId.Trim(), StringComparison.Ordinal))
        {
            return;
        }

        runtimeUnlockBrokenStateRetryBudgetSessionId = null;
        runtimeUnlockBrokenStateRetryBudgetFamily = null;
        runtimeUnlockBrokenStateRetryBudgetStartedUtcMs = 0;
        runtimeUnlockBrokenStateFreshRetriesUsed = 0;
    }

    private static bool IsRuntimeUnlockBrokenStateRetryReason(string normalizedReason)
        => normalizedReason is
            "runtime_unlock_offer_send_not_observed" or
            "runtime_unlock_offer_peer_response_timeout" or
            "runtime_unlock_offer_answer_timeout" or
            "runtime_unlock_answer_ack_timeout" or
            "runtime_unlock_sidecar_unavailable" or
            "runtime_unlock_listener_sidecar_unavailable" or
            "runtime_unlock_sidecar_remote_closed" or
            "runtime_unlock_remote_sidecar_remote_closed";

    private static string GetRuntimeUnlockBrokenStateRetryFamily(string normalizedReason)
        => normalizedReason switch
        {
            "runtime_unlock_sidecar_unavailable" or
            "runtime_unlock_listener_sidecar_unavailable" or
            "runtime_unlock_sidecar_remote_closed" or
            "runtime_unlock_remote_sidecar_remote_closed" => "listener_or_dialer_unavailable",
            "runtime_unlock_offer_answer_timeout" or
            "runtime_unlock_answer_ack_timeout" => "offer_answer_untrusted",
            "runtime_unlock_offer_peer_response_timeout" => "offer_peer_response_not_received",
            _ => "offer_send_not_observed",
        };

    private bool ShouldSuppressRuntimeUnlockRetryAfterTerminalTransactionFailure(string normalizedReason)
    {
        if (!IsRuntimeUnlockBrokenStateRetryReason(normalizedReason))
        {
            return false;
        }

        lock (accelerationGate)
        {
            if (!runtimeUnlockTransactionState.IsTerminal ||
                runtimeUnlockTransactionState.State != RuntimeUnlockTransactionState.Failed)
            {
                return false;
            }

            var failureReason = SanitizeLogToken(runtimeUnlockTransactionState.FailureReason);
            if (string.IsNullOrWhiteSpace(failureReason) ||
                string.Equals(failureReason, "(none)", StringComparison.Ordinal))
            {
                return false;
            }

            if (normalizedReason is
                "runtime_unlock_sidecar_unavailable" or
                "runtime_unlock_listener_sidecar_unavailable" or
                "runtime_unlock_sidecar_remote_closed" or
                "runtime_unlock_remote_sidecar_remote_closed")
            {
                return failureReason.Contains("sidecar", StringComparison.OrdinalIgnoreCase) ||
                       failureReason.Contains("listener", StringComparison.OrdinalIgnoreCase) ||
                       failureReason.Contains("dialer", StringComparison.OrdinalIgnoreCase) ||
                       failureReason.Contains("lease", StringComparison.OrdinalIgnoreCase);
            }

            return failureReason.Contains("runtime_unlock_broken_state", StringComparison.OrdinalIgnoreCase) ||
                   failureReason.Contains("path_probe", StringComparison.OrdinalIgnoreCase) ||
                   failureReason.Contains("precommit_probe", StringComparison.OrdinalIgnoreCase);
        }
    }

    private void MarkRuntimeUnlockOfferAnswerTimeoutScheduledIfCurrent(
        string nonce,
        long payerDecisionId,
        long generation)
    {
        lock (accelerationGate)
        {
            var state = runtimeUnlockOfferProofState;
            if (state is null ||
                state.Retired ||
                state.Generation != generation ||
                state.PayerDecisionId != payerDecisionId ||
                !string.Equals(state.Nonce, nonce, StringComparison.Ordinal))
            {
                return;
            }

            state.AnswerTimeoutScheduled = true;
        }
    }

    private bool RetireRuntimeUnlockOfferIfCurrent(
        string nonce,
        long payerDecisionId,
        long generation,
        string sessionId,
        string reason,
        string retryReason)
    {
        lock (accelerationGate)
        {
            if (outboundAccelerationOfferGeneration != generation ||
                outboundAccelerationOfferPayerDecisionId != payerDecisionId ||
                !string.Equals(outboundAccelerationOfferNonce, nonce, StringComparison.Ordinal))
            {
                return false;
            }

            var state = runtimeUnlockOfferProofState;
            if (state is not null &&
                state.Generation == generation &&
                state.PayerDecisionId == payerDecisionId &&
                string.Equals(state.Nonce, nonce, StringComparison.Ordinal))
            {
                state.Retired = true;
                state.RetiredReason = SanitizeLogToken(reason);
                state.RetryReason = SanitizeLogToken(retryReason);
            }

            RetireOutboundAccelerationOfferLocked(sessionId, reason);
        }

        return true;
    }

    private bool IsRuntimeUnlockOfferGenerationRetired(
        string nonce,
        long payerDecisionId,
        long generation)
    {
        lock (accelerationGate)
        {
            var state = runtimeUnlockOfferProofState;
            return state is not null &&
                   state.Retired &&
                   state.Generation == generation &&
                   state.PayerDecisionId == payerDecisionId &&
                   string.Equals(state.Nonce, nonce, StringComparison.Ordinal);
        }
    }

    private bool InterruptRuntimeUnlockOfferForBridgeRecovery(
        string reason,
        string recoveryReason,
        string trigger)
        => InterruptRuntimeUnlockOfferCriticalSection(
            reason,
            recoveryReason,
            trigger,
            observedLane: null,
            queueLane: null,
            queueClears: 0,
            clearedSinceLast: 0);

    private void HandleRuntimeUnlockOfferQueueCleared(BridgeLifecycleEvent e)
    {
        var lane = string.IsNullOrWhiteSpace(e.QueueLane) ? "unknown" : e.QueueLane.Trim();
        InterruptRuntimeUnlockOfferCriticalSection(
            "offer_interrupted_by_queue_clear",
            $"{SanitizeLogToken(lane)}_queue_cleared",
            "bridge_queue_cleared",
            observedLane: null,
            queueLane: lane,
            queueClears: e.QueueClears,
            clearedSinceLast: e.ClearedSinceLast);
    }

    private bool InterruptRuntimeUnlockOfferCriticalSection(
        string reason,
        string recoveryReason,
        string trigger,
        string? observedLane,
        string? queueLane,
        long queueClears,
        long clearedSinceLast)
    {
        RuntimeUnlockOfferProofState? interruptedState = null;
        lock (accelerationGate)
        {
            var state = runtimeUnlockOfferProofState;
            if (state is null ||
                state.Retired ||
                state.PeerReceived ||
                IsAccelerationNegotiatedAndHealthyUnsafe(state.SessionId) ||
                outboundAccelerationOfferGeneration != state.Generation ||
                outboundAccelerationOfferPayerDecisionId != state.PayerDecisionId ||
                string.IsNullOrWhiteSpace(outboundAccelerationOfferNonce) ||
                !string.Equals(outboundAccelerationOfferNonce, state.Nonce, StringComparison.Ordinal))
            {
                return false;
            }

            if (ShouldPreserveRuntimeUnlockOfferAcrossBridgeInterruptionUnsafe(state, queueLane))
            {
                LogRuntimeUnlockObservedOfferPreservedDuringBridgeInterruption(
                    state,
                    reason,
                    recoveryReason,
                    trigger,
                    observedLane,
                    queueLane,
                    queueClears,
                    clearedSinceLast);
                return false;
            }

            state.Retired = true;
            state.RetiredReason = SanitizeLogToken(reason);
            state.RetryReason = "runtime_unlock_offer_send_not_observed";
            interruptedState = state;
            RetireOutboundAccelerationOfferLocked(state.SessionId, reason);
        }

        if (interruptedState is null)
        {
            return false;
        }

        if (string.Equals(reason, "offer_interrupted_by_bridge_recovery", StringComparison.Ordinal))
        {
            MarkFileTransferTunaActivationBridgeRecoveryStarted(recoveryReason);
        }

        LocalOperationalLog.Warn(
            "NKN.Tuna",
            $"event=tuna_acceleration_activation_offer_not_observed; trigger={SanitizeLogToken(interruptedState.Trigger)}; session_id={SanitizeLogToken(interruptedState.SessionId)}; payer_decision_id={interruptedState.PayerDecisionId}; generation={interruptedState.Generation}; late_answer_window_ms={(long)AccelerationOfferAnswerTimeout.TotalMilliseconds}; retry_scheduled=0; retry_after_recovery_armed=1; replay_scheduled=0; answer_timeout_scheduled=0; pause_deferred=1; recovery_requested=1; recovery_reason={SanitizeLogToken(recoveryReason)}; interruption_reason={SanitizeLogToken(reason)}; interruption_trigger={SanitizeLogToken(trigger)}; observed_send={(interruptedState.ObservedSend ? 1 : 0)}; observed_lane={SanitizeLogToken(observedLane ?? interruptedState.ObservedSendLane)}; queue_lane={SanitizeLogToken(queueLane)}; queue_clears={Math.Max(0, queueClears)}; cleared_since_last={Math.Max(0, clearedSinceLast)}");
        NotifyTransportAccelerationStateChanged("activation_offer_not_observed");
        TryResumeFileTransferDataSessionsAfterTunaActivationNegotiation(
            "tuna_activation_failed_regular_v4_resumed",
            interruptedState.SessionId,
            reason);
        ArmRuntimeUnlockRetryAfterRecovery(
            interruptedState.Generation,
            interruptedState.SessionId,
            "runtime_unlock_offer_send_not_observed",
            recoveryReason,
            requiresLocalListenerRetry: true);
        return true;
    }

    private bool ShouldPreserveRuntimeUnlockOfferAcrossBridgeInterruptionUnsafe(
        RuntimeUnlockOfferProofState state,
        string? queueLane)
        => state.ObservedSend &&
           state.AnswerTimeoutScheduled &&
           !string.IsNullOrWhiteSpace(state.ObservedSendLane);

    private void LogRuntimeUnlockObservedOfferPreservedDuringBridgeInterruption(
        RuntimeUnlockOfferProofState state,
        string reason,
        string recoveryReason,
        string trigger,
        string? observedLane,
        string? queueLane,
        long queueClears,
        long clearedSinceLast)
    {
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_runtime_unlock_observed_offer_preserved; trigger={SanitizeLogToken(state.Trigger)}; session_id={SanitizeLogToken(state.SessionId)}; payer_decision_id={state.PayerDecisionId}; generation={state.Generation}; interruption_reason={SanitizeLogToken(reason)}; interruption_trigger={SanitizeLogToken(trigger)}; recovery_reason={SanitizeLogToken(recoveryReason)}; observed_send={(state.ObservedSend ? 1 : 0)}; observed_lane={SanitizeLogToken(observedLane ?? state.ObservedSendLane)}; peer_received={(state.PeerReceived ? 1 : 0)}; queue_lane={SanitizeLogToken(queueLane)}; queue_clears={Math.Max(0, queueClears)}; cleared_since_last={Math.Max(0, clearedSinceLast)}");
    }

    private void ArmRuntimeUnlockRetryAfterRecovery(
        long retiredOfferGeneration,
        string sessionId,
        string retryReason,
        string recoveryReason)
        => ArmRuntimeUnlockRetryAfterRecovery(
            retiredOfferGeneration,
            sessionId,
            retryReason,
            recoveryReason,
            requiresLocalListenerRetry: false);

    private void ArmRuntimeUnlockRetryAfterRecovery(
        long retiredOfferGeneration,
        string sessionId,
        string retryReason,
        string recoveryReason,
        bool requiresLocalListenerRetry)
    {
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId)
            ? currentSessionSecurityState.SessionId?.Value ?? string.Empty
            : sessionId.Trim();
        if (string.IsNullOrWhiteSpace(normalizedSessionId))
        {
            ScheduleAccelerationNegotiationRetry(retryReason);
            return;
        }

        var transferId = TryGetFirstActiveFileTransferIdForSession(normalizedSessionId);
        RuntimeUnlockRecoveryRetryState? preservedCutThroughState = null;
        lock (accelerationGate)
        {
            var now = DateTimeOffset.UtcNow;
            var nowMs = now.ToUnixTimeMilliseconds();
            var cutThroughReason =
                string.Equals(
                    SanitizeLogToken(retryReason),
                    RuntimeUnlockCutThroughRetryReason,
                    StringComparison.Ordinal) ||
                string.Equals(
                    SanitizeLogToken(recoveryReason),
                    RuntimeUnlockCutThroughRecoveryReason,
                    StringComparison.Ordinal);
            var regularV4CutThroughCandidate =
                IsActiveRegularV4FileTransferRouteHint(normalizedSessionId, transferId) ||
                HasActiveRegularV4FileTransferRouteHint(normalizedSessionId) ||
                (cutThroughReason &&
                 !string.IsNullOrWhiteSpace(transferId) &&
                 !HasActivePostTunaFallbackFileTransferRouteHint(normalizedSessionId) &&
                 !TryGetUnresolvedFileTransferV6TransportEpochForCurrentSession(out _));
            var cutThroughPending = regularV4CutThroughCandidate && cutThroughReason;
            var previousCutThroughAttempt = 0;
            if (cutThroughPending &&
                runtimeUnlockRecoveryRetryState is { } existingCutThroughState &&
                string.Equals(existingCutThroughState.SessionId, normalizedSessionId, StringComparison.Ordinal) &&
                existingCutThroughState.ContractState is not SessionRecoveryContractState.Completed and not SessionRecoveryContractState.Failed &&
                (existingCutThroughState.CutThroughPending ||
                 existingCutThroughState.CutThroughActive ||
                 string.Equals(existingCutThroughState.RetryReason, RuntimeUnlockCutThroughRetryReason, StringComparison.Ordinal) ||
                 string.Equals(existingCutThroughState.RecoveryReason, RuntimeUnlockCutThroughRecoveryReason, StringComparison.Ordinal)))
            {
                previousCutThroughAttempt = Math.Max(0, existingCutThroughState.CutThroughAttempt);
            }

            if (!cutThroughPending &&
                runtimeUnlockRecoveryRetryState is { } existingState &&
                existingState.RetiredOfferGeneration == retiredOfferGeneration &&
                string.Equals(existingState.SessionId, normalizedSessionId, StringComparison.Ordinal) &&
                (existingState.CutThroughPending ||
                 existingState.CutThroughActive ||
                 string.Equals(existingState.RetryReason, RuntimeUnlockCutThroughRetryReason, StringComparison.Ordinal) ||
                 string.Equals(existingState.RecoveryReason, RuntimeUnlockCutThroughRecoveryReason, StringComparison.Ordinal)) &&
                existingState.ContractState is not SessionRecoveryContractState.Completed and not SessionRecoveryContractState.Failed)
            {
                preservedCutThroughState = existingState;
            }
            else
            {
                var contractGeneration = ++runtimeUnlockRecoveryContractNextGeneration;
                runtimeUnlockRecoveryRetryState = new RuntimeUnlockRecoveryRetryState
                {
                    ContractGeneration = contractGeneration,
                    RetiredOfferGeneration = retiredOfferGeneration,
                    SessionId = normalizedSessionId,
                    TransferId = transferId,
                    RetryReason = SanitizeLogToken(retryReason),
                    RecoveryReason = SanitizeLogToken(recoveryReason),
                    CreatedUtcMs = nowMs,
                    RetryDeadlineUtcMs = now.Add(RuntimeUnlockRecoveryContractRetryDeadline).ToUnixTimeMilliseconds(),
                    LivenessDeferralDeadlineUtcMs = now.Add(RuntimeUnlockRecoveryContractLivenessDeferral).ToUnixTimeMilliseconds(),
                    RequiresLocalListenerRetry = requiresLocalListenerRetry,
                    CutThroughPending = cutThroughPending,
                    CutThroughAttempt = previousCutThroughAttempt,
                };
            }
        }

        if (preservedCutThroughState is not null)
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_runtime_unlock_retry_after_recovery_arm_downgrade_ignored; session_id={SanitizeLogToken(normalizedSessionId)}; retired_generation={retiredOfferGeneration}; retry_reason={SanitizeLogToken(retryReason)}; recovery_reason={SanitizeLogToken(recoveryReason)}; preserved_retry_reason={SanitizeLogToken(preservedCutThroughState.RetryReason)}; preserved_recovery_reason={SanitizeLogToken(preservedCutThroughState.RecoveryReason)}; cutthrough_pending={(preservedCutThroughState.CutThroughPending ? 1 : 0)}; cutthrough_active={(preservedCutThroughState.CutThroughActive ? 1 : 0)}");
            LogRuntimeUnlockRecoveryContract("session_recovery_contract_started", normalizedSessionId);
            return;
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_runtime_unlock_retry_after_recovery_armed; session_id={SanitizeLogToken(normalizedSessionId)}; retired_generation={retiredOfferGeneration}; retry_reason={SanitizeLogToken(retryReason)}; recovery_reason={SanitizeLogToken(recoveryReason)}; requires_local_listener_retry={(requiresLocalListenerRetry ? 1 : 0)}");
        LogRuntimeUnlockRecoveryContract("session_recovery_contract_started", normalizedSessionId);
        if (ShouldDeferRuntimeUnlockRetryForActivePostTunaFallbackRepair(normalizedSessionId))
        {
            ScheduleRuntimeUnlockRetryAfterFallbackRepairSoftSettle(
                retiredOfferGeneration,
                normalizedSessionId);
        }
        else if (!IsFileTransferTunaActivationBridgeRecoveryActive())
        {
            ScheduleRuntimeUnlockRetryAfterRecoveryIfArmed("recovery_already_settled");
        }
        else
        {
            ScheduleRuntimeUnlockRetryAfterFallbackRepairSoftSettle(
                retiredOfferGeneration,
                normalizedSessionId);
        }
    }

    private void ScheduleRuntimeUnlockRetryAfterFallbackRepairSoftSettle(
        long retiredOfferGeneration,
        string sessionId)
    {
        var delay = RuntimeUnlockRecoverySoftSettleDelayOverrideForTests ??
            FileTransferTunaActivationBridgeRecoveryWaitOverrideForTests ??
            RuntimeUnlockRecoverySoftSettleDelay;
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(delay, CancellationToken.None).ConfigureAwait(false);
                    if (disposed ||
                        !ShouldSoftSettleRuntimeUnlockRetryAfterFallbackRepair(
                            retiredOfferGeneration,
                            sessionId,
                            out var settleReason))
                    {
                        return;
                    }

                    LocalOperationalLog.Info(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_runtime_unlock_retry_after_fallback_repair_soft_settle; session_id={SanitizeLogToken(sessionId)}; retired_generation={retiredOfferGeneration}; settle_reason={SanitizeLogToken(settleReason)}; delay_ms={(long)delay.TotalMilliseconds}");
                    ScheduleRuntimeUnlockRetryAfterRecoveryIfArmed("post_tuna_fallback_repair_soft_settle");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_runtime_unlock_retry_after_fallback_repair_soft_settle_failed; session_id={SanitizeLogToken(sessionId)}; retired_generation={retiredOfferGeneration}; error={ex.GetType().Name}");
                }
            },
            CancellationToken.None);
    }

    private bool ShouldDeferRuntimeUnlockRetryForActivePostTunaFallbackRepair(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) ||
            !ShouldKeepPostTunaFallbackDataSessionsAvailableDuringTunaActivation(
                sessionId,
                out _))
        {
            return false;
        }

        if (HasRuntimeUnlockRecoveryContractRequiringSidecarDropListenerRearm(sessionId))
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_acceleration_runtime_unlock_retry_after_post_tuna_fallback_listener_rearm_allowed; session_id={SanitizeLogToken(sessionId)}; reason=listener_rearm_must_precede_observed_send_probe");
            return false;
        }

        if (HasRuntimeUnlockRecoveryContractRequiringPeerResponseLocalListenerRetry(sessionId))
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_acceleration_runtime_unlock_retry_after_post_tuna_fallback_listener_rearm_allowed; session_id={SanitizeLogToken(sessionId)}; reason=peer_response_listener_rearm_must_precede_observed_send_probe");
            return false;
        }

        if (HasCurrentFallbackLegAuthorityReceiveProof(sessionId))
        {
            return false;
        }

        if (TryGetCurrentPostTunaFallbackObservedSendProbeProof(
                sessionId,
                out _,
                out _,
                out _))
        {
            return false;
        }

        if (HasSettledRuntimeUnlockPostTunaFallbackAuthorityProbeCandidate(sessionId))
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_acceleration_runtime_unlock_retry_after_post_tuna_fallback_final_probe_allowed; session_id={SanitizeLogToken(sessionId)}; reason=bounded_final_observed_send_probe");
            return false;
        }

        if (TryGetFileTransferFallbackControlProofPendingSnapshot(
                out var pendingSessionId,
                out _,
                out var pendingLanes) &&
            (pendingLanes & NknAccelerationLaneKind.File) == NknAccelerationLaneKind.File &&
            IsSameSessionOrUnknown(pendingSessionId, sessionId))
        {
            return true;
        }

        if (TryGetUnresolvedFileTransferV6TransportEpochForCurrentSession(out var pendingEpoch) &&
            string.Equals(pendingEpoch.SessionId, sessionId.Trim(), StringComparison.Ordinal) &&
            pendingEpoch.TargetTransport == FileTransferTransportKind.RegularNkn)
        {
            return true;
        }

        if (client is not RealNknClientAdapter realClient)
        {
            return false;
        }

        return realClient.TryGetFileTransferRegularV4ActivationSendBlocker(
                   out var blockerReason,
                   out _,
                   includeRegularV4Pressure: false) &&
               IsReceiveStallActivationSendBlocker(blockerReason);
    }

    private bool ShouldDeferRuntimeUnlockRetryForActiveRegularV4ReceiveRecovery(
        string sessionId,
        out string deferReason)
    {
        deferReason = "none";
        if (string.IsNullOrWhiteSpace(sessionId) ||
            !HasActiveRegularV4FileTransferRouteHint(sessionId))
        {
            return false;
        }

        if (!TryGetActiveRegularV4RecoveryLivenessStatus(
                sessionId,
                out var receiveProofObserved,
                out var terminal,
                out var deadlineExpired,
                out var stateReason,
                out var deadlineRemainingMs))
        {
            return false;
        }

        deferReason = stateReason;
        if (receiveProofObserved)
        {
            return false;
        }

        if (HasRuntimeUnlockRecoveryContractRequiringLocalListenerRetry(sessionId))
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_acceleration_runtime_unlock_retry_after_regular_v4_listener_rearm_allowed; session_id={SanitizeLogToken(sessionId)}; liveness_state={SanitizeLogToken(stateReason)}; liveness_deadline_remaining_ms={deadlineRemainingMs}; reason=listener_rearm_must_precede_observed_send_probe");
            deferReason = "active_regular_v4_recovery_listener_rearm_required";
            return false;
        }

        if (HasRuntimeUnlockRecoveryContractRequiringCutThroughProbe(sessionId))
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_acceleration_runtime_unlock_retry_after_regular_v4_cutthrough_allowed; session_id={SanitizeLogToken(sessionId)}; liveness_state={SanitizeLogToken(stateReason)}; liveness_deadline_remaining_ms={deadlineRemainingMs}; reason=cutthrough_must_precede_receive_proof");
            deferReason = "active_regular_v4_recovery_cutthrough_peer_visible_probe";
            return false;
        }

        if (HasSettledRuntimeUnlockRegularV4AuthorityProbeCandidate(sessionId))
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_acceleration_runtime_unlock_retry_after_regular_v4_authority_probe_allowed; session_id={SanitizeLogToken(sessionId)}; liveness_state={SanitizeLogToken(stateReason)}; liveness_deadline_remaining_ms={deadlineRemainingMs}; reason=bounded_contract_observed_send_probe");
            deferReason = "active_regular_v4_recovery_authority_observed_send_probe";
            return false;
        }

        if (terminal || deadlineExpired)
        {
            return true;
        }

        return deadlineRemainingMs > (long)RuntimeUnlockRegularV4FinalObservedSendProbeWindow.TotalMilliseconds;
    }

    private bool TryDeferRuntimeUnlockListenerRearmRetryForActiveRegularV4Recovery(
        string sessionId,
        string listenerRetryReason)
    {
        if (!ShouldDeferRuntimeUnlockListenerRearmRetryForActiveRegularV4Recovery(
                sessionId,
                out var deferReason,
                out var deadlineRemainingMs))
        {
            return false;
        }

        RuntimeUnlockRecoveryRetryState? stateSnapshot = null;
        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            if (state is null ||
                state.ContractState is SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed ||
                !state.RequiresLocalListenerRetry ||
                !IsRuntimeUnlockActivationRetryReason(state.RetryReason) ||
                !string.Equals(state.SessionId, sessionId.Trim(), StringComparison.Ordinal))
            {
                return false;
            }

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            state.Settled = true;
            state.RetryQueued = false;
            state.RetryDispatching = false;
            state.RetryDispatched = false;
            state.RetryAuthorityPending = false;
            state.RetryAuthorityGranted = false;
            state.ObservedSendPending = false;
            state.AuthorizedObservedLane = null;
            state.AuthorityFailureReason = SanitizeLogToken(listenerRetryReason);
            state.ObservedSendDeadlineUtcMs = 0;
            state.RetryDeadlineUtcMs = Math.Max(
                state.RetryDeadlineUtcMs,
                nowMs + (long)RuntimeUnlockRecoveryContractRetryDeadline.TotalMilliseconds);
            state.LivenessDeferralDeadlineUtcMs = Math.Max(
                state.LivenessDeferralDeadlineUtcMs,
                nowMs + (long)RuntimeUnlockRecoveryContractLivenessDeferral.TotalMilliseconds);
            state.ContractState = SessionRecoveryContractState.RecoverySettled;
            stateSnapshot = state;
        }

        LocalOperationalLog.Warn(
            "NKN.Tuna",
            $"event=session_recovery_contract_listener_rearm_deferred_for_regular_v4_recovery; session_id={SanitizeLogToken(sessionId)}; retired_generation={stateSnapshot!.RetiredOfferGeneration}; listener_retry_reason={SanitizeLogToken(listenerRetryReason)}; liveness_state={SanitizeLogToken(deferReason)}; liveness_deadline_remaining_ms={deadlineRemainingMs}; reason=awaiting_regular_v4_receive_recovery_before_listener_rearm");
        LogRuntimeUnlockRecoveryContract("session_recovery_contract_recovery_settled", stateSnapshot.SessionId);
        ScheduleRuntimeUnlockRetryAfterFallbackRepairSoftSettle(
            stateSnapshot.RetiredOfferGeneration,
            stateSnapshot.SessionId);
        return true;
    }

    private bool ShouldDeferRuntimeUnlockListenerRearmRetryForActiveRegularV4Recovery(
        string sessionId,
        out string deferReason,
        out long deadlineRemainingMs)
    {
        deferReason = "none";
        deadlineRemainingMs = 0;
        if (string.IsNullOrWhiteSpace(sessionId) ||
            !HasActiveRegularV4FileTransferRouteHint(sessionId))
        {
            return false;
        }

        if (TryGetActiveRegularV4RecoveryLivenessStatus(
                sessionId,
                out var receiveProofObserved,
                out var terminal,
                out var deadlineExpired,
                out var stateReason,
                out deadlineRemainingMs))
        {
            deferReason = stateReason;
            return !receiveProofObserved && !terminal && !deadlineExpired;
        }

        if (client is RealNknClientAdapter realClient &&
            realClient.TryGetFileTransferRegularV4ActivationSendBlocker(
                out var blockerReason,
                out var blockerRemainingMs,
                includeRegularV4Pressure: false) &&
            IsReceiveStallActivationSendBlocker(blockerReason))
        {
            deferReason = blockerReason;
            deadlineRemainingMs = blockerRemainingMs;
            return true;
        }

        return false;
    }

    private bool HasRuntimeUnlockRecoveryContractRequiringLocalListenerRetry(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var normalizedSessionId = sessionId.Trim();
        var peerResponseRetryNeedsListenerRearm = false;
        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            peerResponseRetryNeedsListenerRearm =
                state is not null &&
                state.RequiresLocalListenerRetry &&
                !state.ListenerRearmCompleted &&
                !state.RetryObserved &&
                string.Equals(state.RetryReason, RuntimeUnlockCutThroughRetryReason, StringComparison.Ordinal) &&
                state.ContractState is not (SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed) &&
                string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal);
        }

        if (!peerResponseRetryNeedsListenerRearm &&
            IsCurrentPostTunaFallbackAuthorityBridgeRecoveryInProgress(
                normalizedSessionId,
                out _))
        {
            return false;
        }

        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            return state is not null &&
                   state.RequiresLocalListenerRetry &&
                   !state.ListenerRearmCompleted &&
                   !state.RetryObserved &&
                   IsRuntimeUnlockActivationRetryReason(state.RetryReason) &&
                   state.ContractState is not (SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed) &&
                   string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal);
        }
    }

    private bool HasRuntimeUnlockRecoveryContractRequiringPeerResponseLocalListenerRetry(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var normalizedSessionId = sessionId.Trim();
        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            return state is not null &&
                   state.RequiresLocalListenerRetry &&
                   !state.ListenerRearmCompleted &&
                   !state.RetryObserved &&
                   string.Equals(state.RetryReason, RuntimeUnlockCutThroughRetryReason, StringComparison.Ordinal) &&
                   state.ContractState is not (SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed) &&
                   string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal);
        }
    }

    private bool HasRuntimeUnlockRecoveryContractCompletedPeerResponseListenerRearmForPostTunaFallback(
        string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var normalizedSessionId = sessionId.Trim();
        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            return state is not null &&
                   state.RequiresLocalListenerRetry &&
                   state.ListenerRearmCompleted &&
                   !state.RetryObserved &&
                   string.Equals(state.RetryReason, RuntimeUnlockCutThroughRetryReason, StringComparison.Ordinal) &&
                   state.ContractState is not (SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed) &&
                   string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal);
        }
    }

    private bool HasRuntimeUnlockRecoveryContractRequiringLocalListenerRetryForControlProofPreflight(
        string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var normalizedSessionId = sessionId.Trim();
        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            return state is not null &&
                   state.RequiresLocalListenerRetry &&
                   !state.ListenerRearmCompleted &&
                   !state.RetryObserved &&
                   IsRuntimeUnlockActivationRetryReason(state.RetryReason) &&
                   (IsSessionRecoveryContractRetryRequired(state) ||
                    state.ContractState is SessionRecoveryContractState.RecoverySettled or
                        SessionRecoveryContractState.RetryQueued or
                        SessionRecoveryContractState.RetryDispatched ||
                    state.RetryAuthorityPending) &&
                   state.ContractState is not (SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed) &&
                   string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal);
        }
    }

    private bool HasRuntimeUnlockRecoveryContractRequiringSidecarDropListenerRearm(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var normalizedSessionId = sessionId.Trim();
        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            return state is not null &&
                   state.RequiresLocalListenerRetry &&
                   !state.ListenerRearmCompleted &&
                   !state.RetryObserved &&
                   IsRuntimeUnlockSidecarDropListenerRearmRetryReason(state.RetryReason) &&
                   state.ContractState is not (SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed) &&
                   string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal);
        }
    }

    private bool HasRuntimeUnlockRecoveryContractRequiringCutThroughProbe(
        string sessionId,
        long retiredOfferGeneration = 0)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var normalizedSessionId = sessionId.Trim();
        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            return state is not null &&
                   state.CutThroughPending &&
                   !state.CutThroughActive &&
                   !state.CutThroughCompleted &&
                   !state.RetryQueued &&
                   !state.RetryObserved &&
                   (retiredOfferGeneration <= 0 || state.RetiredOfferGeneration == retiredOfferGeneration) &&
                   string.Equals(state.RetryReason, RuntimeUnlockCutThroughRetryReason, StringComparison.Ordinal) &&
                   state.ContractState is not (SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed) &&
                   string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal);
        }
    }

    private bool ShouldAllowRuntimeUnlockRegularV4CutThroughProbeAfterSoftSettle(
        long retiredOfferGeneration,
        string sessionId,
        string stateReason,
        long deadlineRemainingMs,
        out string settleReason)
    {
        settleReason = "none";
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var normalizedSessionId = sessionId.Trim();
        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            if (state is null ||
                !state.CutThroughPending ||
                state.CutThroughActive ||
                state.CutThroughCompleted ||
                state.RetryQueued ||
                state.RetryObserved ||
                state.RetiredOfferGeneration != retiredOfferGeneration ||
                !string.Equals(state.RetryReason, RuntimeUnlockCutThroughRetryReason, StringComparison.Ordinal) ||
                state.ContractState is SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed ||
                !string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal))
            {
                return false;
            }

            state.Settled = true;
            state.AuthorityFailureReason = null;
            state.ContractState = SessionRecoveryContractState.RecoverySettled;
        }

        settleReason = "active_regular_v4_recovery_cutthrough_peer_visible_probe";
        LocalOperationalLog.Warn(
            "NKN.Tuna",
            $"event=tuna_acceleration_runtime_unlock_retry_after_regular_v4_cutthrough_allowed; session_id={SanitizeLogToken(normalizedSessionId)}; retired_generation={retiredOfferGeneration}; liveness_state={SanitizeLogToken(stateReason)}; liveness_deadline_remaining_ms={deadlineRemainingMs}; reason=cutthrough_must_precede_receive_proof");
        return true;
    }

    private bool HasSettledRuntimeUnlockRegularV4AuthorityProbeCandidate(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var normalizedSessionId = sessionId.Trim();
        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            return state is not null &&
                   state.Settled &&
                   !state.RetryQueued &&
                   !state.RetryObserved &&
                   !IsRuntimeUnlockRegularV4ReceiveRecoveryPendingAuthorityReason(state.AuthorityFailureReason) &&
                   IsRuntimeUnlockActivationRetryReason(state.RetryReason) &&
                   state.ContractState is not (SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed) &&
                   string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal);
        }
    }

    private bool HasSettledRuntimeUnlockPostTunaFallbackAuthorityProbeCandidate(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var normalizedSessionId = sessionId.Trim();
        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            return state is not null &&
                   state.Settled &&
                   !state.RetryObserved &&
                   string.Equals(state.AuthorityFailureReason, RuntimeUnlockPostTunaFallbackFinalProbeReason, StringComparison.Ordinal) &&
                   IsRuntimeUnlockActivationRetryReason(state.RetryReason) &&
                   state.ContractState is not (SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed) &&
                   string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal);
        }
    }

    private static bool IsRuntimeUnlockPostTunaFallbackFinalProbeAuthoritySnapshot(
        RuntimeUnlockRecoveryRetryState state,
        out long deadlineRemainingMs)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var retryRemainingMs = state.RetryDeadlineUtcMs <= 0
            ? long.MaxValue
            : state.RetryDeadlineUtcMs - nowMs;
        var livenessRemainingMs = state.LivenessDeferralDeadlineUtcMs <= 0
            ? long.MaxValue
            : state.LivenessDeferralDeadlineUtcMs - nowMs;
        deadlineRemainingMs = Math.Max(0, Math.Min(retryRemainingMs, livenessRemainingMs));
        return state is { RetryDispatched: true, RetryAuthorityPending: true, RetryObserved: false } &&
               string.Equals(state.AuthorityFailureReason, RuntimeUnlockPostTunaFallbackFinalProbeReason, StringComparison.Ordinal) &&
               deadlineRemainingMs <= (long)RuntimeUnlockRegularV4FinalObservedSendProbeWindow.TotalMilliseconds;
    }

    private bool ShouldAllowRuntimeUnlockRegularV4AuthorityProbeAfterSoftSettle(
        long retiredOfferGeneration,
        string sessionId,
        out string settleReason)
    {
        settleReason = "none";
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var normalizedSessionId = sessionId.Trim();
        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            if (state is null ||
                state.RetryQueued ||
                state.RetryObserved ||
                state.RetiredOfferGeneration != retiredOfferGeneration ||
                !state.Settled ||
                !IsRuntimeUnlockActivationRetryReason(state.RetryReason) ||
                state.ContractState is SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed ||
                !string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal))
            {
                return false;
            }

            state.AuthorityFailureReason = RuntimeUnlockRegularV4AuthorityProbeReason;
        }

        settleReason = "active_regular_v4_recovery_authority_observed_send_probe";
        LocalOperationalLog.Warn(
            "NKN.Tuna",
            $"event=tuna_acceleration_runtime_unlock_retry_after_regular_v4_authority_probe_allowed; session_id={SanitizeLogToken(normalizedSessionId)}; retired_generation={retiredOfferGeneration}; reason=bounded_contract_observed_send_probe");
        return true;
    }

    private bool TryMarkRuntimeUnlockPostTunaFallbackFinalAuthorityProbeCandidate(
        long retiredOfferGeneration,
        string sessionId,
        string stateReason,
        out string settleReason,
        out long deadlineRemainingMs)
    {
        settleReason = "none";
        deadlineRemainingMs = long.MaxValue;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var normalizedSessionId = sessionId.Trim();
        if (IsCurrentPostTunaFallbackAuthorityBridgeRecoveryInProgress(
                normalizedSessionId,
                out var authorityReason))
        {
            settleReason = authorityReason;
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_runtime_unlock_retry_after_post_tuna_fallback_final_probe_deferred; session_id={SanitizeLogToken(normalizedSessionId)}; retired_generation={retiredOfferGeneration}; liveness_state={SanitizeLogToken(stateReason)}; authority_reason={SanitizeLogToken(authorityReason)}; reason=fallback_authority_bridge_recovery_in_progress");
            return false;
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            if (state is null ||
                state.RetryObserved ||
                state.ObservedSendPending ||
                state.RetiredOfferGeneration != retiredOfferGeneration ||
                !IsRuntimeUnlockActivationRetryReason(state.RetryReason) ||
                state.ContractState is SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed ||
                !string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal))
            {
                return false;
            }

            var retryRemainingMs = state.RetryDeadlineUtcMs > 0
                ? state.RetryDeadlineUtcMs - nowMs
                : long.MaxValue;
            var livenessRemainingMs = state.LivenessDeferralDeadlineUtcMs > 0
                ? state.LivenessDeferralDeadlineUtcMs - nowMs
                : long.MaxValue;
            deadlineRemainingMs = Math.Max(0, Math.Min(retryRemainingMs, livenessRemainingMs));
            if (deadlineRemainingMs > (long)RuntimeUnlockRegularV4FinalObservedSendProbeWindow.TotalMilliseconds)
            {
                return false;
            }

            state.Settled = true;
            state.RetryQueued = false;
            state.RetryDispatching = false;
            state.RetryDispatched = false;
            state.RetryAuthorityPending = false;
            state.RetryAuthorityGranted = false;
            state.AuthorizedObservedLane = null;
            state.ObservedSendDeadlineUtcMs = 0;
            state.AuthorityFailureReason = RuntimeUnlockPostTunaFallbackFinalProbeReason;
            state.ContractState = SessionRecoveryContractState.RecoverySettled;
        }

        settleReason = "active_post_tuna_fallback_final_observed_send_probe";
        LocalOperationalLog.Warn(
            "NKN.Tuna",
            $"event=tuna_acceleration_runtime_unlock_retry_after_post_tuna_fallback_authority_probe_allowed; session_id={SanitizeLogToken(normalizedSessionId)}; retired_generation={retiredOfferGeneration}; liveness_state={SanitizeLogToken(stateReason)}; liveness_deadline_remaining_ms={deadlineRemainingMs}; reason=bounded_final_observed_send_probe");
        return true;
    }

    private bool TryMarkRuntimeUnlockRegularV4BridgeCompletedAuthorityProbeCandidate(
        long retiredOfferGeneration,
        string sessionId,
        string stateReason,
        out string settleReason)
    {
        settleReason = "none";
        if (!string.Equals(
                stateReason,
                "regular_v4_bridge_recovery_completed_awaiting_filetransfer_proof",
                StringComparison.Ordinal))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var normalizedSessionId = sessionId.Trim();
        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            if (state is null ||
                state.RetryQueued ||
                state.RetryObserved ||
                state.RetiredOfferGeneration != retiredOfferGeneration ||
                !IsRuntimeUnlockActivationRetryReason(state.RetryReason) ||
                state.ContractState is SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed ||
                !string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal))
            {
                return false;
            }

            state.Settled = true;
            state.AuthorityFailureReason = RuntimeUnlockRegularV4AuthorityProbeReason;
            state.ContractState = SessionRecoveryContractState.RecoverySettled;
        }

        settleReason = "active_regular_v4_bridge_completed_authority_observed_send_probe";
        LocalOperationalLog.Warn(
            "NKN.Tuna",
            $"event=tuna_acceleration_runtime_unlock_retry_after_regular_v4_bridge_completed_authority_probe_allowed; session_id={SanitizeLogToken(normalizedSessionId)}; retired_generation={retiredOfferGeneration}; liveness_state={SanitizeLogToken(stateReason)}; reason=bounded_contract_observed_send_probe");
        return true;
    }

    private bool TryMarkRuntimeUnlockRegularV4StartedRecoveryAuthorityProbeCandidate(
        long retiredOfferGeneration,
        string sessionId,
        string stateReason,
        out string settleReason)
    {
        settleReason = "none";
        if (!string.Equals(
                stateReason,
                "regular_v4_bridge_recovery_started_awaiting_filetransfer_proof",
                StringComparison.Ordinal))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var normalizedSessionId = sessionId.Trim();
        var livenessSessionId = SanitizeLogToken(sessionId);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        FileTransferRegularV4RecoveryLivenessState? regularV4State;
        lock (fileTransferFallbackProofGate)
        {
            regularV4State = fileTransferRegularV4RecoveryLivenessState;
            if (regularV4State is null ||
                !string.Equals(regularV4State.SessionId, livenessSessionId, StringComparison.Ordinal) ||
                !string.Equals(regularV4State.RouteToken, FileTransferRouteResolver.RegularNknV4FastToken, StringComparison.Ordinal) ||
                regularV4State.ProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
                !IsRegularV4BridgeRecoveryStartedRuntimeUnlockProbeReadyUnsafe(regularV4State, nowMs))
            {
                return false;
            }
        }

        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            if (state is null ||
                state.RetryQueued ||
                state.RetryObserved ||
                state.RetiredOfferGeneration != retiredOfferGeneration ||
                !IsRuntimeUnlockActivationRetryReason(state.RetryReason) ||
                state.ContractState is SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed ||
                !string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal))
            {
                return false;
            }

            state.Settled = true;
            state.AuthorityFailureReason = RuntimeUnlockRegularV4AuthorityProbeReason;
            state.ContractState = SessionRecoveryContractState.RecoverySettled;
        }

        settleReason = "active_regular_v4_started_recovery_expired_authority_observed_send_probe";
        LocalOperationalLog.Warn(
            "NKN.Tuna",
            $"event=tuna_acceleration_runtime_unlock_retry_after_regular_v4_started_recovery_expired_authority_probe_allowed; session_id={SanitizeLogToken(normalizedSessionId)}; retired_generation={retiredOfferGeneration}; liveness_state={SanitizeLogToken(stateReason)}; probe_delay_ms={(long)RuntimeUnlockRegularV4BridgeRecoveryStartedAuthorityProbeDelay.TotalMilliseconds}; reason=bounded_contract_observed_send_probe");
        return true;
    }

    private bool TryGetCurrentPostTunaFallbackAuthorityProbeState(
        string sessionId,
        out bool stable,
        out string reason)
    {
        stable = false;
        reason = "none";
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var normalizedSessionId = sessionId.Trim();
        lock (fileTransferFallbackProofGate)
        {
            if (fileTransferFallbackLegAuthorityState is not { } state ||
                !string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal) ||
                !string.Equals(state.RouteToken, FileTransferRouteResolver.PostTunaFallbackV6Token, StringComparison.Ordinal) ||
                state.ProtocolVersion != FileTransferProtocol.ProtocolVersionV6)
            {
                return false;
            }

            if (state.RecoveryExhausted)
            {
                reason = "fallback_authority_recovery_exhausted";
                return true;
            }

            if (!state.ReceiveProofObserved)
            {
                reason = state.BridgeRecoveryStarted && !state.BridgeRecoveryCompleted
                    ? "fallback_authority_bridge_recovery_in_progress"
                    : "fallback_authority_awaiting_receive_proof";
                return true;
            }

            stable = true;
            reason = state.Completed
                ? "fallback_authority_completed_receive_proof"
                : "fallback_authority_receive_proof";
            return true;
        }
    }

    private bool HasCurrentFallbackLegAuthorityReceiveProof(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        return TryGetCurrentPostTunaFallbackAuthorityProbeState(
                   sessionId,
                   out var stable,
                   out _) &&
               stable;
    }

    private bool IsCurrentPostTunaFallbackAuthorityBridgeRecoveryInProgress(
        string sessionId,
        out string reason)
    {
        reason = "none";
        return TryGetCurrentPostTunaFallbackAuthorityProbeState(
                   sessionId,
                   out var stable,
                   out reason) &&
               !stable &&
               string.Equals(
                   reason,
                   "fallback_authority_bridge_recovery_in_progress",
                   StringComparison.Ordinal);
    }

    private bool HasUnsettledPostTunaFallbackV6TransportEpoch(string sessionId, out FileTransferV6TransportEpochSnapshot snapshot)
    {
        snapshot = default!;
        if (string.IsNullOrWhiteSpace(sessionId) ||
            !TryGetUnresolvedFileTransferV6TransportEpochForCurrentSession(out var pendingEpoch) ||
            !string.Equals(pendingEpoch.SessionId, sessionId.Trim(), StringComparison.Ordinal) ||
            !IsPostTunaFallbackRegularNknEpoch(pendingEpoch))
        {
            return false;
        }

        snapshot = pendingEpoch;
        return pendingEpoch.State is not V6TransportEpochState.Recovered and not V6TransportEpochState.Terminal;
    }

    private bool TryGetCurrentPostTunaFallbackObservedSendProbeProof(
        string sessionId,
        out string proof,
        out string proofDirection,
        out long proofAgeMs)
    {
        proof = "none";
        proofDirection = "none";
        proofAgeMs = 0;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        if (TryGetCurrentPostTunaFallbackAuthorityProbeState(
                sessionId,
                out var authorityStable,
                out var authorityReason))
        {
            if (!authorityStable)
            {
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_runtime_unlock_retry_after_post_tuna_fallback_stability_deferred; session_id={SanitizeLogToken(sessionId)}; reason={SanitizeLogToken(authorityReason)}");
                return false;
            }

            proof = authorityReason;
            proofDirection = "fallback_leg_authority";
            return true;
        }

        if (HasUnsettledPostTunaFallbackV6TransportEpoch(sessionId, out var pendingEpoch))
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_runtime_unlock_retry_after_post_tuna_fallback_stability_deferred; session_id={SanitizeLogToken(sessionId)}; transfer_id={SanitizeLogToken(pendingEpoch.TransferId)}; transport_epoch={pendingEpoch.TransportEpoch}; state={FormatFileTransferV6TransportEpochStateForLog(pendingEpoch.State)}; reason=post_tuna_fallback_v6_epoch_unsettled");
            return false;
        }

        if (HasFreshPostTunaFallbackReceiverFrontierProofHint(sessionId, out var proofHint))
        {
            proof = proofHint.ProofKind;
            proofDirection = proofHint.Direction;
            proofAgeMs = Math.Max(0, (long)(DateTimeOffset.UtcNow - proofHint.ObservedUtc).TotalMilliseconds);
            return true;
        }

        return false;
    }

    private bool ShouldSoftSettleRuntimeUnlockRetryAfterFallbackRepair(
        long retiredOfferGeneration,
        string sessionId,
        out string settleReason)
    {
        settleReason = "none";
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? string.Empty : sessionId.Trim();
        if (string.IsNullOrWhiteSpace(normalizedSessionId))
        {
            return false;
        }

        var currentSessionId = currentSessionSecurityState.SessionId?.Value;
        if (string.IsNullOrWhiteSpace(currentSessionId) ||
            !string.Equals(currentSessionId.Trim(), normalizedSessionId, StringComparison.Ordinal))
        {
            return false;
        }

        if (TryGetFileTransferFallbackControlProofPendingSnapshot(
                out var pendingSessionId,
                out var pendingReason,
                out var pendingLanes) &&
            (pendingLanes & NknAccelerationLaneKind.File) == NknAccelerationLaneKind.File &&
            IsSameSessionOrUnknown(pendingSessionId, normalizedSessionId))
        {
            if (HasRuntimeUnlockRecoveryContractRequiringPeerResponseLocalListenerRetry(normalizedSessionId))
            {
                settleReason = "active_post_tuna_fallback_listener_rearm_required";
                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_runtime_unlock_retry_after_post_tuna_fallback_listener_rearm_allowed; session_id={SanitizeLogToken(normalizedSessionId)}; retired_generation={retiredOfferGeneration}; blocker_reason={SanitizeLogToken(pendingReason)}; lanes={FormatAccelerationLanesForLog(pendingLanes)}; reason=peer_response_listener_rearm_must_precede_observed_send_probe");
                return true;
            }

            if (HasRuntimeUnlockRecoveryContractCompletedPeerResponseListenerRearmForPostTunaFallback(
                    normalizedSessionId))
            {
                settleReason = "active_post_tuna_fallback_listener_rearm_completed";
                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_runtime_unlock_retry_after_post_tuna_fallback_listener_rearm_completed_allowed; session_id={SanitizeLogToken(normalizedSessionId)}; retired_generation={retiredOfferGeneration}; blocker_reason={SanitizeLogToken(pendingReason)}; lanes={FormatAccelerationLanesForLog(pendingLanes)}; reason=listener_rearm_completed_must_dispatch_fresh_offer");
                return true;
            }

            if (TryGetCurrentPostTunaFallbackObservedSendProbeProof(
                    normalizedSessionId,
                    out var proof,
                    out var proofDirection,
                    out var proofAgeMs))
            {
                settleReason = $"active_post_tuna_fallback_current_{proof}";
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_runtime_unlock_retry_after_fallback_repair_authority_probe_allowed; session_id={SanitizeLogToken(normalizedSessionId)}; retired_generation={retiredOfferGeneration}; blocker_reason={SanitizeLogToken(pendingReason)}; lanes={FormatAccelerationLanesForLog(pendingLanes)}; proof={SanitizeLogToken(proof)}; proof_direction={SanitizeLogToken(proofDirection)}; proof_age_ms={proofAgeMs}; reason=same_session_post_tuna_fallback_observed_send_probe");
                return true;
            }

            lock (accelerationGate)
            {
                var state = runtimeUnlockRecoveryRetryState;
                if (state is null ||
                    state.RetryQueued ||
                    state.RetiredOfferGeneration != retiredOfferGeneration ||
                    !string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            if (TryMarkRuntimeUnlockPostTunaFallbackFinalAuthorityProbeCandidate(
                    retiredOfferGeneration,
                    normalizedSessionId,
                    pendingReason,
                    out settleReason,
                    out var finalProbeDeadlineRemainingMs))
            {
                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_runtime_unlock_retry_after_post_tuna_fallback_final_probe_allowed; session_id={SanitizeLogToken(normalizedSessionId)}; retired_generation={retiredOfferGeneration}; blocker_reason={SanitizeLogToken(pendingReason)}; blocker_remaining_ms=0; liveness_deadline_remaining_ms={finalProbeDeadlineRemainingMs}; reason=bounded_final_observed_send_probe");
                return true;
            }

            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_acceleration_runtime_unlock_retry_after_fallback_repair_soft_settle_deferred; session_id={SanitizeLogToken(normalizedSessionId)}; retired_generation={retiredOfferGeneration}; blocker_reason={SanitizeLogToken(pendingReason)}; blocker_remaining_ms=0; file_state=pending_control_proof; fallback_epoch=0");
            ScheduleRuntimeUnlockRetryAfterFallbackRepairSoftSettle(
                retiredOfferGeneration,
                normalizedSessionId);
            return false;
        }

        if (HasActiveRegularV4FileTransferRouteHint(normalizedSessionId))
        {
            lock (accelerationGate)
            {
                var state = runtimeUnlockRecoveryRetryState;
                if (state is null ||
                    state.RetryQueued ||
                    state.RetiredOfferGeneration != retiredOfferGeneration ||
                    !string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            if (TryGetActiveRegularV4RecoveryLivenessStatus(
                    normalizedSessionId,
                    out var receiveProofObserved,
                    out var terminal,
                    out var deadlineExpired,
                    out var livenessStateReason,
                    out var livenessDeadlineRemainingMs))
            {
                if (receiveProofObserved)
                {
                    settleReason = "active_regular_v4_recovery_receive_proof";
                    return true;
                }

                if (terminal || deadlineExpired)
                {
                    var failureReason = terminal
                        ? livenessStateReason
                        : "regular_v4_receive_proof_deadline_expired";
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_runtime_unlock_retry_after_regular_v4_not_scheduled; session_id={SanitizeLogToken(normalizedSessionId)}; retired_generation={retiredOfferGeneration}; liveness_state={SanitizeLogToken(livenessStateReason)}; liveness_deadline_remaining_ms={livenessDeadlineRemainingMs}; reason={SanitizeLogToken(failureReason)}");
                    FailRuntimeUnlockRecoveryContractIfActive(
                        normalizedSessionId,
                        failureReason);
                    return false;
                }

                if (HasRuntimeUnlockRecoveryContractRequiringLocalListenerRetry(normalizedSessionId))
                {
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_runtime_unlock_retry_after_regular_v4_listener_rearm_allowed; session_id={SanitizeLogToken(normalizedSessionId)}; retired_generation={retiredOfferGeneration}; liveness_state={SanitizeLogToken(livenessStateReason)}; liveness_deadline_remaining_ms={livenessDeadlineRemainingMs}; reason=listener_rearm_must_precede_observed_send_probe");
                    settleReason = "active_regular_v4_recovery_listener_rearm_required";
                    return true;
                }

                if (ShouldAllowRuntimeUnlockRegularV4CutThroughProbeAfterSoftSettle(
                        retiredOfferGeneration,
                        normalizedSessionId,
                        livenessStateReason,
                        livenessDeadlineRemainingMs,
                        out settleReason))
                {
                    return true;
                }

                if (livenessDeadlineRemainingMs <= (long)RuntimeUnlockRegularV4FinalObservedSendProbeWindow.TotalMilliseconds)
                {
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_runtime_unlock_retry_after_regular_v4_final_probe_allowed; session_id={SanitizeLogToken(normalizedSessionId)}; retired_generation={retiredOfferGeneration}; liveness_state={SanitizeLogToken(livenessStateReason)}; liveness_deadline_remaining_ms={livenessDeadlineRemainingMs}; reason=bounded_final_observed_send_probe");
                    settleReason = "active_regular_v4_recovery_final_observed_send_probe";
                    return true;
                }

                if (TryMarkRuntimeUnlockRegularV4BridgeCompletedAuthorityProbeCandidate(
                        retiredOfferGeneration,
                        normalizedSessionId,
                        livenessStateReason,
                        out settleReason))
                {
                    return true;
                }

                if (TryMarkRuntimeUnlockRegularV4StartedRecoveryAuthorityProbeCandidate(
                        retiredOfferGeneration,
                        normalizedSessionId,
                        livenessStateReason,
                        out settleReason))
                {
                    return true;
                }

                if (ShouldAllowRuntimeUnlockRegularV4AuthorityProbeAfterSoftSettle(
                        retiredOfferGeneration,
                        normalizedSessionId,
                        out settleReason))
                {
                    return true;
                }

                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_runtime_unlock_retry_after_regular_v4_soft_settle_deferred; session_id={SanitizeLogToken(normalizedSessionId)}; retired_generation={retiredOfferGeneration}; blocker_reason={SanitizeLogToken(livenessStateReason)}; blocker_remaining_ms={livenessDeadlineRemainingMs}; reason=awaiting_validated_filetransfer_receive_proof");
                ScheduleRuntimeUnlockRetryAfterFallbackRepairSoftSettle(
                    retiredOfferGeneration,
                    normalizedSessionId);
                return false;
            }

            if (client is RealNknClientAdapter regularV4RealClient &&
                regularV4RealClient.TryGetFileTransferRegularV4ActivationSendBlocker(
                    out var regularV4BlockerReason,
                    out var regularV4BlockerRemainingMs,
                    includeRegularV4Pressure: false) &&
                IsReceiveStallActivationSendBlocker(regularV4BlockerReason))
            {
                if (ShouldAllowRuntimeUnlockRegularV4CutThroughProbeAfterSoftSettle(
                        retiredOfferGeneration,
                        normalizedSessionId,
                        regularV4BlockerReason,
                        regularV4BlockerRemainingMs,
                        out settleReason))
                {
                    return true;
                }

                if (ShouldAllowRuntimeUnlockRegularV4AuthorityProbeAfterSoftSettle(
                        retiredOfferGeneration,
                        normalizedSessionId,
                        out settleReason))
                {
                    return true;
                }

                if (ShouldDeferRuntimeUnlockSoftSettleForReceiveStallBlocker(
                        regularV4BlockerReason,
                        regularV4BlockerRemainingMs))
                {
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_runtime_unlock_retry_after_regular_v4_soft_settle_deferred; session_id={SanitizeLogToken(normalizedSessionId)}; retired_generation={retiredOfferGeneration}; blocker_reason={SanitizeLogToken(regularV4BlockerReason)}; blocker_remaining_ms={regularV4BlockerRemainingMs}");
                    ScheduleRuntimeUnlockRetryAfterFallbackRepairSoftSettle(
                        retiredOfferGeneration,
                        normalizedSessionId);
                    return false;
                }

                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_runtime_unlock_retry_after_regular_v4_soft_settle_elapsed; session_id={SanitizeLogToken(normalizedSessionId)}; retired_generation={retiredOfferGeneration}; blocker_reason={SanitizeLogToken(regularV4BlockerReason)}; blocker_remaining_ms={regularV4BlockerRemainingMs}");
                settleReason = "active_regular_v4_recovery_soft_settle_elapsed";
                return true;
            }

            settleReason = "active_regular_v4_recovery_soft_settle";
            return true;
        }

        TunaFallbackLaneState fileState;
        long fallbackEpoch;
        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            if (state is null ||
                state.RetryQueued ||
                state.RetiredOfferGeneration != retiredOfferGeneration ||
                !string.Equals(state.SessionId, normalizedSessionId, StringComparison.Ordinal) ||
                !TryGetCurrentTunaFallbackProofStateUnsafe(normalizedSessionId, out var fallbackState) ||
                (fallbackState.Lanes & NknAccelerationLaneKind.File) != NknAccelerationLaneKind.File ||
                fallbackState.FileState is TunaFallbackLaneState.None or TunaFallbackLaneState.Recovered)
            {
                return false;
            }

            fileState = fallbackState.FileState;
            fallbackEpoch = fallbackState.Epoch;
        }

        if (client is RealNknClientAdapter realClient &&
            realClient.TryGetFileTransferRegularV4ActivationSendBlocker(
                out var blockerReason,
                out var blockerRemainingMs,
                includeRegularV4Pressure: false) &&
            IsReceiveStallActivationSendBlocker(blockerReason))
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_acceleration_runtime_unlock_retry_after_fallback_repair_soft_settle_deferred; session_id={SanitizeLogToken(normalizedSessionId)}; retired_generation={retiredOfferGeneration}; blocker_reason={SanitizeLogToken(blockerReason)}; blocker_remaining_ms={blockerRemainingMs}; file_state={FormatTunaFallbackLaneState(fileState)}; fallback_epoch={fallbackEpoch}");
            ScheduleRuntimeUnlockRetryAfterFallbackRepairSoftSettle(
                retiredOfferGeneration,
                normalizedSessionId);
            return false;
        }

        if (TryGetUnresolvedFileTransferV6TransportEpochForCurrentSession(out var pendingEpoch) &&
            IsPostTunaFallbackRegularNknEpoch(pendingEpoch) &&
            ShouldAllowRuntimeUnlockRetryForActivePostTunaFallbackRepair(pendingEpoch))
        {
            settleReason = "active_post_tuna_fallback_v6_repair";
            return true;
        }

        if (HasActivePostTunaFallbackFileTransferRouteHint(normalizedSessionId))
        {
            settleReason = $"active_post_tuna_fallback_route_{FormatTunaFallbackLaneState(fileState)}_{fallbackEpoch}";
            return true;
        }

        return false;
    }

    private void ScheduleRuntimeUnlockRetryAfterRecoveryIfArmed(string trigger)
    {
        RuntimeUnlockRecoveryRetryState? stateSnapshot;
        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            if (state is null || state.RetryQueued)
            {
                return;
            }

            if (state.RetryDispatching ||
                state.RetryDispatched ||
                state.ObservedSendPending ||
                state.CutThroughActive)
            {
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_runtime_unlock_recovery_settle_ignored_after_dispatch; session_id={SanitizeLogToken(state.SessionId)}; retired_generation={state.RetiredOfferGeneration}; trigger={SanitizeLogToken(trigger)}; retry_dispatching={(state.RetryDispatching ? 1 : 0)}; retry_dispatched={(state.RetryDispatched ? 1 : 0)}; observed_send_pending={(state.ObservedSendPending ? 1 : 0)}; cutthrough_pending={(state.CutThroughPending ? 1 : 0)}; cutthrough_active={(state.CutThroughActive ? 1 : 0)}");
                return;
            }

            var currentSessionId = currentSessionSecurityState.SessionId?.Value;
            if (string.IsNullOrWhiteSpace(currentSessionId) ||
                !string.Equals(currentSessionId.Trim(), state.SessionId, StringComparison.Ordinal))
            {
                runtimeUnlockRecoveryRetryState = null;
                return;
            }

            stateSnapshot = state;
        }

        if (stateSnapshot is null)
        {
            return;
        }

        if (ShouldDeferRuntimeUnlockRetryForActiveRegularV4ReceiveRecovery(
                stateSnapshot.SessionId,
                out var regularV4DeferReason))
        {
            lock (accelerationGate)
            {
                var state = runtimeUnlockRecoveryRetryState;
                if (state is null ||
                    state.RetryQueued ||
                    state.RetiredOfferGeneration != stateSnapshot.RetiredOfferGeneration ||
                    !string.Equals(state.SessionId, stateSnapshot.SessionId, StringComparison.Ordinal))
                {
                    return;
                }

                state.Settled = true;
                state.ContractState = SessionRecoveryContractState.RecoverySettled;
            }

            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_runtime_unlock_retry_after_recovery_deferred_for_regular_v4_receive_proof; session_id={SanitizeLogToken(stateSnapshot.SessionId)}; retired_generation={stateSnapshot.RetiredOfferGeneration}; retry_reason={SanitizeLogToken(stateSnapshot.RetryReason)}; recovery_reason={SanitizeLogToken(stateSnapshot.RecoveryReason)}; trigger={SanitizeLogToken(trigger)}; liveness_state={SanitizeLogToken(regularV4DeferReason)}");
            LogRuntimeUnlockRecoveryContract("session_recovery_contract_recovery_settled", stateSnapshot.SessionId);
            ScheduleRuntimeUnlockRetryAfterFallbackRepairSoftSettle(
                stateSnapshot.RetiredOfferGeneration,
                stateSnapshot.SessionId);
            return;
        }

        if (ShouldDeferRuntimeUnlockRetryForActivePostTunaFallbackRepair(stateSnapshot.SessionId))
        {
            lock (accelerationGate)
            {
                var state = runtimeUnlockRecoveryRetryState;
                if (state is null ||
                    state.RetryQueued ||
                    state.RetiredOfferGeneration != stateSnapshot.RetiredOfferGeneration ||
                    !string.Equals(state.SessionId, stateSnapshot.SessionId, StringComparison.Ordinal))
                {
                    return;
                }

                state.Settled = true;
                state.ContractState = SessionRecoveryContractState.RecoverySettled;
            }

            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_runtime_unlock_retry_after_recovery_deferred_for_fallback_repair; session_id={SanitizeLogToken(stateSnapshot.SessionId)}; retired_generation={stateSnapshot.RetiredOfferGeneration}; retry_reason={SanitizeLogToken(stateSnapshot.RetryReason)}; recovery_reason={SanitizeLogToken(stateSnapshot.RecoveryReason)}; trigger={SanitizeLogToken(trigger)}");
            LogRuntimeUnlockRecoveryContract("session_recovery_contract_recovery_settled", stateSnapshot.SessionId);
            ScheduleRuntimeUnlockRetryAfterFallbackRepairSoftSettle(
                stateSnapshot.RetiredOfferGeneration,
                stateSnapshot.SessionId);
            return;
        }

        RuntimeUnlockRecoveryRetryState? stateToSchedule = null;
        var queuedBehindActiveNegotiation = false;
        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            if (state is null ||
                state.RetryQueued ||
                state.RetiredOfferGeneration != stateSnapshot.RetiredOfferGeneration ||
                !string.Equals(state.SessionId, stateSnapshot.SessionId, StringComparison.Ordinal))
            {
                return;
            }

            state.Settled = true;
            state.RetryQueued = true;
            queuedBehindActiveNegotiation = Volatile.Read(ref accelerationNegotiationScheduled) != 0;
            state.QueuedBehindActiveNegotiation = queuedBehindActiveNegotiation;
            GrantRuntimeUnlockRecoveryContractRetryAuthorityUnsafe(state);
            state.ContractState = SessionRecoveryContractState.RetryQueued;
            stateToSchedule = state;
        }

        if (stateToSchedule is null)
        {
            return;
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled; session_id={SanitizeLogToken(stateToSchedule.SessionId)}; retired_generation={stateToSchedule.RetiredOfferGeneration}; retry_reason={SanitizeLogToken(stateToSchedule.RetryReason)}; recovery_reason={SanitizeLogToken(stateToSchedule.RecoveryReason)}; trigger={SanitizeLogToken(trigger)}; queued_behind_active_negotiation={(queuedBehindActiveNegotiation ? 1 : 0)}");
        LogRuntimeUnlockRecoveryContract("session_recovery_contract_recovery_settled", stateToSchedule.SessionId);
        LogRuntimeUnlockRecoveryContract("session_recovery_contract_retry_authority_granted", stateToSchedule.SessionId);
        LogRuntimeUnlockRecoveryContract("session_recovery_contract_retry_queued", stateToSchedule.SessionId);
        ScheduleAccelerationNegotiationRetry(stateToSchedule.RetryReason);
    }

    private async Task<bool> SendAccelerationPayerIntentAsync(
        string target,
        string sessionId,
        string envelopeCode,
        string localRole,
        NknAccelerationLaneKind lanes,
        bool canOfferListener,
        string trigger,
        long payerDecisionId,
        CancellationToken ct)
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var intent = canOfferListener ? "will_listen" : "dialer_only";
        var payloadModel = new TransportAccelerationPayerIntentPayload
        {
            SessionId = sessionId,
            SenderRole = localRole,
            Intent = intent,
            SupportedLanes = NknAccelerationLaneCodec.ToNames(lanes),
            Trigger = SanitizeLogToken(trigger),
            PayerDecisionId = payerDecisionId,
            SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ExpiresAtUnixMs = DateTimeOffset.UtcNow.Add(AccelerationOfferLifetime).ToUnixTimeMilliseconds(),
            Nonce = nonce,
            SidecarProtocolVersion = TunaSidecarProtocolVersion,
        };
        var payload = CreateSecureControlPayload(
            MsgType.TransportAccelerationPayerIntent,
            nonce,
            JsonSerializer.SerializeToUtf8Bytes(payloadModel));
        var envelope = CreateEnvelope(envelopeCode, MsgType.TransportAccelerationPayerIntent, payload, replyTo: null);
        var queued = await SendAccelerationControlEnvelopeWithBulkBypassAsync(
                target,
                envelope,
                "payer_intent",
                ct)
            .ConfigureAwait(false);
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_payer_intent_{(queued.Succeeded ? "queued" : "rejected")}; intent={intent}; role={SanitizeLogToken(localRole)}; trigger={SanitizeLogToken(trigger)}; lanes={FormatAccelerationLanesForLog(lanes)}; payer_decision_id={payerDecisionId}");
        return queued.Succeeded;
    }

    private void HandleTransportAccelerationPayerIntent(string source, Envelope env)
    {
        if (!TryDecryptControlPayload(source, env, MsgType.TransportAccelerationPayerIntent, out var securePayload))
        {
            return;
        }

        if (!TryDeserializeAccelerationPayload<TransportAccelerationPayerIntentPayload>(securePayload.Plaintext, out var intent) ||
            intent is null)
        {
            RejectAccelerationEnvelope("transport_acceleration_payer_intent", "payload_invalid", env.MessageId);
            return;
        }

        if (!TryValidateControlSecureMetadata("transport_acceleration_payer_intent", securePayload.Metadata, intent.Nonce, env.MessageId))
        {
            return;
        }

        var validation = ValidateAccelerationPayerIntent(source, intent);
        if (!validation.IsValid)
        {
            RejectAccelerationEnvelope("transport_acceleration_payer_intent", validation.Reason ?? "invalid", env.MessageId);
            return;
        }

        ClearAccelerationUserStoppedForFreshPeerMessage("payer_intent", intent.Trigger, intent.SentAtUnixMs);
        ObserveRemotePayerIntentForPayerPriority(intent, validation);
        if (ShouldYieldLocalPaidListenerToRemoteHelpeeIntent(intent))
        {
            YieldLocalPaidListenerToRemoteHelpee("payer_intent_will_listen", intent.PayerDecisionId);
        }

        if (IsHelpeeSessionRole(ResolveLocalSessionRole()) &&
            IsHelperSessionRole(intent.SenderRole) &&
            accelerationLane is INknTunaAccelerationSession { CanOfferListener: true } &&
            !IsAccelerationNegotiatedAndHealthy())
        {
            ScheduleAccelerationNegotiationIfEligible("remote_payer_intent");
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_payer_intent_received; intent={SanitizeLogToken(intent.Intent)}; sender_role={SanitizeLogToken(intent.SenderRole)}; lanes={FormatAccelerationLanesForLog(validation.AcceptedLanes)}; payer_decision_id={intent.PayerDecisionId}");
    }

    private void HandleTransportAccelerationOffer(string source, Envelope env)
    {
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_offer_received_raw; msg_id={SanitizeLogToken(env.MessageId)}");
        if (!TryDecryptControlPayload(source, env, MsgType.TransportAccelerationOffer, out var securePayload))
        {
            return;
        }

        if (!TryDeserializeAccelerationPayload<TransportAccelerationOfferPayload>(securePayload.Plaintext, out var offer) ||
            offer is null)
        {
            RejectAccelerationEnvelope("transport_acceleration_offer", "payload_invalid", env.MessageId);
            return;
        }

        if (!TryValidateControlSecureMetadata("transport_acceleration_offer", securePayload.Metadata, offer.Nonce, env.MessageId))
        {
            return;
        }

        SendAccelerationLifecycleAckIfValid(source, env, "transport_acceleration_offer");

        _ = Task.Run(
            () => HandleTransportAccelerationOfferAsync(source, offer, env.MessageId, CancellationToken.None),
            CancellationToken.None);
    }

    private async Task HandleTransportAccelerationOfferAsync(
        string source,
        TransportAccelerationOfferPayload offer,
        string messageId,
        CancellationToken ct)
    {
        var validation = ValidateAccelerationOffer(source, offer);
        if (validation.IsHardReject)
        {
            RejectAccelerationEnvelope("transport_acceleration_offer", validation.Reason ?? "invalid", messageId);
            return;
        }

        if (validation.IsValid)
        {
            ClearAccelerationUserStoppedForFreshPeerMessage("offer", offer.Trigger, offer.SentAtUnixMs);
        }

        if (validation.IsValid && IsAccelerationUserStoppedForCurrentSession())
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                "event=tuna_acceleration_offer_rejected; reason=user_stopped_tuna");
            await SendAccelerationAnswerAsync(
                offer,
                accepted: false,
                lanes: NknAccelerationLaneKind.None,
                rejectReason: "user_stopped_tuna",
                pendingAnswerAckGeneration: 0,
                ct).ConfigureAwait(false);
            return;
        }

        if (validation.IsValid &&
            IsRuntimeUnlockActivationReason(offer.Trigger) &&
            TryGetPendingRuntimeUnlockAnswerAckForOffer(
                offer,
                out var pendingAnswerAckGeneration,
                out var pendingAnswerAckLanes,
                out var matchesPendingAnswerAck))
        {
            if (matchesPendingAnswerAck)
            {
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_runtime_unlock_offer_duplicate_pending_answer_ack; session_id={SanitizeLogToken(offer.SessionId)}; payer_decision_id={offer.PayerDecisionId}; generation={pendingAnswerAckGeneration}; action=answer_replay");
                await SendAccelerationAnswerAsync(
                        offer,
                        accepted: true,
                        lanes: pendingAnswerAckLanes,
                        rejectReason: null,
                        pendingAnswerAckGeneration: pendingAnswerAckGeneration,
                        ct: ct)
                    .ConfigureAwait(false);
            }
            else
            {
                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_runtime_unlock_offer_rejected_pending_answer_ack; session_id={SanitizeLogToken(offer.SessionId)}; payer_decision_id={offer.PayerDecisionId}; generation={pendingAnswerAckGeneration}; action=reject; reject_reason=answer_ack_pending");
                await SendAccelerationAnswerAsync(
                        offer,
                        accepted: false,
                        lanes: NknAccelerationLaneKind.None,
                        rejectReason: "answer_ack_pending",
                        pendingAnswerAckGeneration: 0,
                        ct: ct)
                    .ConfigureAwait(false);
            }

            return;
        }

        ObserveRemoteOfferForPayerPriority(offer, validation);
        if (validation.IsValid && ShouldRejectRemoteHelperOfferForHelpeePriority(offer))
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                "event=tuna_acceleration_offer_rejected; reason=helpee_payer_preferred; sender_role=helper");
            ScheduleAccelerationNegotiationIfEligible("helpee_payer_preferred");
            await SendAccelerationAnswerAsync(
                offer,
                accepted: false,
                lanes: NknAccelerationLaneKind.None,
                rejectReason: "helpee_payer_preferred",
                pendingAnswerAckGeneration: 0,
                ct).ConfigureAwait(false);
            return;
        }

        var rejectReason = validation.Reason;
        if (validation.IsValid)
        {
            if (ShouldPauseRegularNknV4FileTransferForRuntimeUnlock(offer.SessionId))
            {
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=filetransfer_tuna_activation_negotiation_regular_nkn_pause_deferred; session_id={SanitizeLogToken(offer.SessionId)}; reason=peer_offer_answer_ack_pending; trigger={SanitizeLogToken(offer.Trigger)}");
            }

            NotifyTransportAccelerationStateChanged("dialer_starting");
        }

        var accepted = validation.IsValid &&
                       accelerationLane is INknTunaAccelerationSession tunaSession &&
                       await tunaSession.StartDialerSidecarAsync(offer.TunaAddress, source, ct).ConfigureAwait(false);
        if (accepted &&
            !validation.AllowsStaleRemotePayerDecision &&
            IsStaleRemotePayerDecision(offer.PayerDecisionId))
        {
            accepted = false;
            rejectReason = "stale_payer_decision";
            ScheduleAccelerationLaneStop("stale_payer_decision");
        }

        if (!accepted && rejectReason is null)
        {
            rejectReason = "sidecar_unavailable";
        }

        if (!accepted)
        {
            ResumeFileTransferDataSessionsAfterTunaActivationNegotiation(
                rejectReason ?? "sidecar_unavailable",
                offer.SessionId,
                "dialer_not_accepted");
        }

        if (accepted)
        {
            NotifyTransportAccelerationStateChanged("dialer_ready");
            var answerAckGeneration = BeginPendingAccelerationAnswerAck(offer, validation.AcceptedLanes);
            MarkInboundRuntimeUnlockOfferAccepted(offer, answerAckGeneration);
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_answer_ack_pending; session_id={SanitizeLogToken(offer.SessionId)}; lanes={FormatAccelerationLanesForLog(validation.AcceptedLanes)}; payer_decision_id={offer.PayerDecisionId}; generation={answerAckGeneration}");
            ScheduleAccelerationAnswerAckTimeout(offer.SessionId, offer.Nonce, offer.PayerDecisionId, answerAckGeneration);
        }

        await SendAccelerationAnswerAsync(
                offer,
                accepted,
                accepted ? validation.AcceptedLanes : NknAccelerationLaneKind.None,
                rejectReason,
                accepted ? GetPendingAccelerationAnswerAckGeneration() : 0,
                ct)
            .ConfigureAwait(false);
    }

    private long BeginPendingAccelerationAnswerAck(
        TransportAccelerationOfferPayload offer,
        NknAccelerationLaneKind lanes)
    {
        var sessionId = string.IsNullOrWhiteSpace(offer.SessionId)
            ? currentSessionSecurityState.SessionId?.Value ?? string.Empty
            : offer.SessionId.Trim();
        var nonce = string.IsNullOrWhiteSpace(offer.Nonce) ? string.Empty : offer.Nonce.Trim();
        lock (accelerationGate)
        {
            RetireOutboundAccelerationOfferLocked(sessionId, "accepted_peer_offer");
            accelerationSessionId = null;
            accelerationNegotiatedLanes = NknAccelerationLaneKind.None;
            pendingAccelerationAnswerAckSessionId = sessionId;
            pendingAccelerationAnswerAckNonce = nonce;
            pendingAccelerationAnswerAckTrigger = SanitizeLogToken(offer.Trigger);
            pendingAccelerationAnswerAckLanes = lanes;
            pendingAccelerationAnswerAckPayerDecisionId = offer.PayerDecisionId;
            return ++pendingAccelerationAnswerAckGeneration;
        }
    }

    private void RetireOutboundAccelerationOfferLocked(string sessionId, string reason)
    {
        if (string.IsNullOrWhiteSpace(outboundAccelerationOfferNonce))
        {
            return;
        }

        retiredAccelerationOfferSessionId = sessionId;
        retiredAccelerationOfferNonce = outboundAccelerationOfferNonce;
        retiredAccelerationOfferTrigger = outboundAccelerationOfferTrigger;
        retiredAccelerationOfferPayerDecisionId = outboundAccelerationOfferPayerDecisionId;
        retiredAccelerationOfferExpiresUtcMs = DateTimeOffset.UtcNow
            .Add(AccelerationOfferAnswerTimeout)
            .ToUnixTimeMilliseconds();
        ClearOutboundAccelerationOfferLocked();
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_outbound_offer_retired; reason={SanitizeLogToken(reason)}; session_id={SanitizeLogToken(sessionId)}; payer_decision_id={retiredAccelerationOfferPayerDecisionId}");
    }

    private bool RetireRuntimeUnlockOfferForPendingAnswerLocked(string reason)
    {
        if (runtimeUnlockOfferProofState is not { Retired: false } state ||
            string.IsNullOrWhiteSpace(outboundAccelerationOfferNonce) ||
            outboundAccelerationOfferGeneration != state.Generation ||
            outboundAccelerationOfferPayerDecisionId != state.PayerDecisionId ||
            !string.Equals(outboundAccelerationOfferNonce, state.Nonce, StringComparison.Ordinal) ||
            !IsRuntimeUnlockActivationReason(outboundAccelerationOfferTrigger))
        {
            return false;
        }

        if (!state.ObservedSend &&
            !state.PeerReceived &&
            !state.AnswerTimeoutScheduled)
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_pending_runtime_unlock_answer_not_preserved; reason={SanitizeLogToken(reason)}; session_id={SanitizeLogToken(state.SessionId)}; payer_decision_id={state.PayerDecisionId}; generation={state.Generation}; observed_send=0; peer_received=0; answer_timeout_scheduled=0");
            return false;
        }

        state.Retired = true;
        state.RetiredReason = SanitizeLogToken(reason);
        state.RetryReason = "runtime_unlock_pending_answer_preserved";
        RetireOutboundAccelerationOfferLocked(state.SessionId, reason);
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_pending_runtime_unlock_answer_preserved; reason={SanitizeLogToken(reason)}; session_id={SanitizeLogToken(state.SessionId)}; payer_decision_id={state.PayerDecisionId}; generation={state.Generation}; observed_send={(state.ObservedSend ? 1 : 0)}; answer_timeout_scheduled={(state.AnswerTimeoutScheduled ? 1 : 0)}");
        return true;
    }

    private bool ShouldPreserveRuntimeUnlockPendingAnswerAcrossResetLocked(string reason)
    {
        var normalizedReason = SanitizeLogToken(reason);
        if (normalizedReason is not "sidecar_payer_yield_to_helpee" and not
            "payer_yield_to_helpee")
        {
            return false;
        }

        if (RetireRuntimeUnlockOfferForPendingAnswerLocked($"{normalizedReason}_pending_runtime_unlock_answer"))
        {
            return true;
        }

        var state = runtimeUnlockOfferProofState;
        return state is
               {
                   Retired: true,
               } &&
               IsRuntimeUnlockPendingAnswerRetiredReason(state.RetiredReason) &&
               !string.IsNullOrWhiteSpace(retiredAccelerationOfferNonce) &&
               string.Equals(retiredAccelerationOfferNonce, state.Nonce, StringComparison.Ordinal) &&
               string.Equals(retiredAccelerationOfferSessionId, state.SessionId, StringComparison.Ordinal) &&
               DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < retiredAccelerationOfferExpiresUtcMs;
    }

    private bool TryCaptureInterruptedRuntimeUnlockOfferResetRetryLocked(
        string reason,
        out string sessionId,
        out long payerDecisionId,
        out long generation,
        out string retryReason)
    {
        var normalizedReason = SanitizeLogToken(reason);
        sessionId = string.Empty;
        payerDecisionId = 0;
        generation = 0;
        retryReason = string.Empty;

        if (normalizedReason is not "sidecar_remote_closed" and not "remote_sidecar_remote_closed")
        {
            return false;
        }

        if (runtimeUnlockOfferProofState is not { Retired: false } state ||
            string.IsNullOrWhiteSpace(outboundAccelerationOfferNonce) ||
            outboundAccelerationOfferGeneration != state.Generation ||
            outboundAccelerationOfferPayerDecisionId != state.PayerDecisionId ||
            !string.Equals(outboundAccelerationOfferNonce, state.Nonce, StringComparison.Ordinal) ||
            !IsRuntimeUnlockActivationReason(outboundAccelerationOfferTrigger))
        {
            return false;
        }

        if (!state.ObservedSend &&
            !state.PeerReceived &&
            !state.AnswerTimeoutScheduled)
        {
            return false;
        }

        var retiredReason = $"{normalizedReason}_runtime_unlock_offer_reset";
        retryReason = $"runtime_unlock_{normalizedReason}_after_observed_send";
        state.Retired = true;
        state.RetiredReason = retiredReason;
        state.RetryReason = retryReason;
        sessionId = state.SessionId;
        payerDecisionId = state.PayerDecisionId;
        generation = state.Generation;
        RetireOutboundAccelerationOfferLocked(state.SessionId, retiredReason);
        return true;
    }

    private bool TryCaptureUnobservedRuntimeUnlockOfferResetRetryLocked(
        string reason,
        out string sessionId,
        out long payerDecisionId,
        out long generation,
        out string retryReason)
    {
        var normalizedReason = SanitizeLogToken(reason);
        sessionId = string.Empty;
        payerDecisionId = 0;
        generation = 0;
        retryReason = string.Empty;

        if (normalizedReason is not "sidecar_remote_closed" and not "remote_sidecar_remote_closed")
        {
            return false;
        }

        if (runtimeUnlockOfferProofState is not { Retired: false } state ||
            string.IsNullOrWhiteSpace(outboundAccelerationOfferNonce) ||
            outboundAccelerationOfferGeneration != state.Generation ||
            outboundAccelerationOfferPayerDecisionId != state.PayerDecisionId ||
            !string.Equals(outboundAccelerationOfferNonce, state.Nonce, StringComparison.Ordinal) ||
            !IsRuntimeUnlockActivationReason(outboundAccelerationOfferTrigger))
        {
            return false;
        }

        if (state.ObservedSend ||
            state.PeerReceived ||
            state.AnswerTimeoutScheduled)
        {
            return false;
        }

        retryReason = $"runtime_unlock_{normalizedReason}";
        state.Retired = true;
        state.RetiredReason = $"{normalizedReason}_unobserved_runtime_unlock_offer_reset";
        state.RetryReason = retryReason;
        sessionId = state.SessionId;
        payerDecisionId = state.PayerDecisionId;
        generation = state.Generation;
        return true;
    }

    private static bool IsRuntimeUnlockPendingAnswerRetiredReason(string? reason)
        => reason is
            "payer_yield_pending_runtime_unlock_answer" or
            "sidecar_remote_closed_pending_runtime_unlock_answer" or
            "remote_sidecar_remote_closed_pending_runtime_unlock_answer" or
            "sidecar_payer_yield_to_helpee_pending_runtime_unlock_answer" or
            "payer_yield_to_helpee_pending_runtime_unlock_answer" or
            "runtime_unlock_offer_peer_response_timeout_pending_runtime_unlock_answer";

    private bool HasPendingRuntimeUnlockAnswerTimeoutTarget(string nonce)
    {
        lock (accelerationGate)
        {
            if (string.Equals(outboundAccelerationOfferNonce, nonce, StringComparison.Ordinal) &&
                IsRuntimeUnlockActivationReason(outboundAccelerationOfferTrigger))
            {
                return true;
            }

            var state = runtimeUnlockOfferProofState;
            return state is { Retired: true } &&
                   IsRuntimeUnlockPendingAnswerRetiredReason(state.RetiredReason) &&
                   !string.IsNullOrWhiteSpace(retiredAccelerationOfferNonce) &&
                   string.Equals(retiredAccelerationOfferNonce, nonce, StringComparison.Ordinal) &&
                   string.Equals(retiredAccelerationOfferNonce, state.Nonce, StringComparison.Ordinal) &&
                   string.Equals(retiredAccelerationOfferSessionId, state.SessionId, StringComparison.Ordinal) &&
                   DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < retiredAccelerationOfferExpiresUtcMs;
        }
    }

    private void ScheduleRuntimeUnlockOfferPeerResponseTimeout(
        string nonce,
        long payerDecisionId,
        long generation,
        string sessionId,
        string? observedLane)
    {
        var timeout = RuntimeUnlockOfferPeerResponseTimeoutOverrideForTests ??
            RuntimeUnlockOfferPeerResponseTimeout;
        if (timeout <= TimeSpan.Zero)
        {
            return;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(timeout, CancellationToken.None).ConfigureAwait(false);
                    if (disposed || IsAccelerationNegotiatedAndHealthy())
                    {
                        return;
                    }

                    RuntimeUnlockOfferProofState? timedOutOffer = null;
                    RuntimeUnlockRecoveryRetryState? cutThroughTimedOutState = null;
                    lock (accelerationGate)
                    {
                        var offer = runtimeUnlockOfferProofState;
                        var contract = runtimeUnlockRecoveryRetryState;
                        if (offer is null ||
                            contract is null ||
                            offer.Retired ||
                            offer.PeerReceived ||
                            offer.Generation != generation ||
                            offer.PayerDecisionId != payerDecisionId ||
                            !string.Equals(offer.Nonce, nonce, StringComparison.Ordinal) ||
                            !offer.ObservedSend ||
                            !offer.AnswerTimeoutScheduled ||
                            contract.ContractState is SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed ||
                            !string.Equals(contract.SessionId, sessionId.Trim(), StringComparison.Ordinal) ||
                            contract.CurrentOfferGeneration != generation ||
                            !contract.ObservedSendPending ||
                            contract.RetryObserved ||
                            outboundAccelerationOfferGeneration != generation ||
                            outboundAccelerationOfferPayerDecisionId != payerDecisionId ||
                            !string.Equals(outboundAccelerationOfferNonce, nonce, StringComparison.Ordinal))
                        {
                            return;
                        }

                        var pendingAnswerPreserved = RetireRuntimeUnlockOfferForPendingAnswerLocked(
                            "runtime_unlock_offer_peer_response_timeout_pending_runtime_unlock_answer");
                        if (!pendingAnswerPreserved)
                        {
                            offer.Retired = true;
                            offer.RetiredReason = "runtime_unlock_offer_peer_response_timeout";
                            offer.RetryReason = "runtime_unlock_offer_peer_response_timeout";
                            RetireOutboundAccelerationOfferLocked(offer.SessionId, "runtime_unlock_offer_peer_response_timeout");
                        }

                        contract.ObservedSendPending = false;
                        contract.RetryAuthorityPending = false;
                        contract.AuthorityFailureReason = "runtime_unlock_offer_peer_response_timeout";
                        if (contract.CutThroughActive &&
                            !contract.CutThroughPeerReceived)
                        {
                            cutThroughTimedOutState = contract;
                            if (contract.CutThroughAttempt >= RuntimeUnlockCutThroughMaxAttempts)
                            {
                                contract.AuthorityFailureReason = "runtime_unlock_peer_response_not_received";
                                contract.ContractState = SessionRecoveryContractState.Failed;
                            }
                        }

                        timedOutOffer = offer;
                    }

                    if (timedOutOffer is null)
                    {
                        return;
                    }

                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_runtime_unlock_offer_peer_response_timeout; timeout_ms={(long)timeout.TotalMilliseconds}; session_id={SanitizeLogToken(timedOutOffer.SessionId)}; payer_decision_id={timedOutOffer.PayerDecisionId}; generation={timedOutOffer.Generation}; observed_lane={SanitizeLogToken(observedLane ?? timedOutOffer.ObservedSendLane)}");
                    NotifyTransportAccelerationStateChanged("activation_offer_not_observed");
                    TryResumeFileTransferDataSessionsAfterTunaActivationNegotiation(
                        "tuna_activation_failed_regular_v4_resumed",
                        timedOutOffer.SessionId,
                        "runtime_unlock_offer_peer_response_timeout");

                    if (cutThroughTimedOutState is not null)
                    {
                        if (cutThroughTimedOutState.CutThroughAttempt < RuntimeUnlockCutThroughMaxAttempts &&
                            TryGetRuntimeUnlockRegularV4ReceiveRecoveryBlocker(
                                out var cutThroughBlockerReason,
                                out var cutThroughBlockerRemainingMs))
                        {
                            var cutThroughRecoveryRequested = TryRequestFileTransferTunaActivationOfferSendRecovery(
                                "offer_peer_response",
                                timedOutOffer.SessionId,
                                "peer_response_timeout_without_peer_response",
                                out var cutThroughRecoveryReason,
                                out var cutThroughRecoverySessionId);
                            ArmRuntimeUnlockRetryAfterRecovery(
                                timedOutOffer.Generation,
                                cutThroughRecoverySessionId ?? timedOutOffer.SessionId,
                                RuntimeUnlockCutThroughRetryReason,
                                cutThroughRecoveryReason ?? RuntimeUnlockCutThroughRecoveryReason,
                                requiresLocalListenerRetry: false);
                            LocalOperationalLog.Warn(
                                "NKN.Tuna",
                                $"event=runtime_unlock_cutthrough_retry_after_recovery_armed; session_id={SanitizeLogToken(timedOutOffer.SessionId)}; payer_decision_id={timedOutOffer.PayerDecisionId}; offer_generation={timedOutOffer.Generation}; contract_generation={cutThroughTimedOutState.ContractGeneration}; attempt={cutThroughTimedOutState.CutThroughAttempt}; max_attempts={RuntimeUnlockCutThroughMaxAttempts}; recovery_requested={(cutThroughRecoveryRequested ? 1 : 0)}; blocker_reason={SanitizeLogToken(cutThroughBlockerReason)}; blocker_remaining_ms={cutThroughBlockerRemainingMs}; recovery_reason={SanitizeLogToken(cutThroughRecoveryReason ?? RuntimeUnlockCutThroughRecoveryReason)}; reason=regular_v4_receive_recovery_still_blocking_peer_response");
                            LocalOperationalLog.Warn(
                                "NKN.Tuna",
                                $"event=tuna_acceleration_activation_offer_not_observed; trigger={SanitizeLogToken(timedOutOffer.Trigger)}; session_id={SanitizeLogToken(timedOutOffer.SessionId)}; payer_decision_id={timedOutOffer.PayerDecisionId}; generation={timedOutOffer.Generation}; interruption_reason=runtime_unlock_offer_peer_response_timeout; retry_scheduled=0; retry_after_recovery_armed=1; replay_scheduled=0; answer_timeout_scheduled=0; recovery_requested={(cutThroughRecoveryRequested ? 1 : 0)}; recovery_reason={SanitizeLogToken(cutThroughRecoveryReason ?? RuntimeUnlockCutThroughRecoveryReason)}; blocker_reason={SanitizeLogToken(cutThroughBlockerReason)}");
                            return;
                        }

                        RuntimeUnlockRecoveryRetryState? failedCutThroughState = null;
                        lock (accelerationGate)
                        {
                            var state = runtimeUnlockRecoveryRetryState;
                            if (state is not null &&
                                state.ContractGeneration == cutThroughTimedOutState.ContractGeneration &&
                                state.CurrentOfferGeneration == timedOutOffer.Generation &&
                                state.CutThroughActive &&
                                !state.CutThroughPeerReceived &&
                                state.ContractState is not SessionRecoveryContractState.Completed and not SessionRecoveryContractState.Failed)
                            {
                                state.AuthorityFailureReason = "runtime_unlock_peer_response_not_received";
                                state.ContractState = SessionRecoveryContractState.Failed;
                                failedCutThroughState = state;
                            }
                            else if (state is not null &&
                                     state.ContractGeneration == cutThroughTimedOutState.ContractGeneration &&
                                     state.ContractState == SessionRecoveryContractState.Failed)
                            {
                                failedCutThroughState = state;
                            }
                        }

                        LocalOperationalLog.Warn(
                            "NKN.Tuna",
                            $"event=runtime_unlock_cutthrough_failed; session_id={SanitizeLogToken(cutThroughTimedOutState.SessionId)}; payer_decision_id={timedOutOffer.PayerDecisionId}; offer_generation={timedOutOffer.Generation}; contract_generation={cutThroughTimedOutState.ContractGeneration}; attempt={cutThroughTimedOutState.CutThroughAttempt}; max_attempts={RuntimeUnlockCutThroughMaxAttempts}; reason=runtime_unlock_peer_response_not_received");
                        if (failedCutThroughState is not null)
                        {
                            LogRuntimeUnlockRecoveryContract(
                                "session_recovery_contract_failed",
                                failedCutThroughState.SessionId);
                        }

                        LocalOperationalLog.Warn(
                            "NKN.Tuna",
                            $"event=tuna_acceleration_activation_offer_not_observed; trigger={SanitizeLogToken(timedOutOffer.Trigger)}; session_id={SanitizeLogToken(timedOutOffer.SessionId)}; payer_decision_id={timedOutOffer.PayerDecisionId}; generation={timedOutOffer.Generation}; interruption_reason=runtime_unlock_offer_peer_response_timeout; retry_scheduled=0; retry_after_recovery_armed=0; replay_scheduled=0; answer_timeout_scheduled=0; recovery_requested=0; recovery_reason=runtime_unlock_peer_response_not_received");
                        return;
                    }

                    if (TryDeferRuntimeUnlockForRegularV4ReceiveRecovery(
                            timedOutOffer.SessionId,
                            "peer_response_timeout_without_peer_response",
                            timedOutOffer.PayerDecisionId,
                            timedOutOffer.Generation,
                            out var blockerReason,
                            out _))
                    {
                        LocalOperationalLog.Warn(
                            "NKN.Tuna",
                            $"event=tuna_acceleration_activation_offer_not_observed; trigger={SanitizeLogToken(timedOutOffer.Trigger)}; session_id={SanitizeLogToken(timedOutOffer.SessionId)}; payer_decision_id={timedOutOffer.PayerDecisionId}; generation={timedOutOffer.Generation}; interruption_reason=runtime_unlock_offer_peer_response_timeout; retry_scheduled=0; retry_after_recovery_armed=0; replay_scheduled=0; answer_timeout_scheduled=0; recovery_requested=0; recovery_reason=regular_v4_receive_recovery_unproven; blocker_reason={SanitizeLogToken(blockerReason)}");
                        return;
                    }

                    var recoveryRequested = TryRequestFileTransferTunaActivationOfferSendRecovery(
                        "offer_peer_response",
                        timedOutOffer.SessionId,
                        "peer_response_timeout_without_peer_response",
                        out var recoveryReason,
                        out var recoverySessionId);
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_activation_offer_not_observed; trigger={SanitizeLogToken(timedOutOffer.Trigger)}; session_id={SanitizeLogToken(timedOutOffer.SessionId)}; payer_decision_id={timedOutOffer.PayerDecisionId}; generation={timedOutOffer.Generation}; interruption_reason=runtime_unlock_offer_peer_response_timeout; retry_scheduled={(recoveryRequested ? 0 : 1)}; retry_after_recovery_armed={(recoveryRequested ? 1 : 0)}; replay_scheduled=0; answer_timeout_scheduled=0; recovery_requested={(recoveryRequested ? 1 : 0)}; recovery_reason={SanitizeLogToken(recoveryReason)}");
                    var listenerReadyReuse = ShouldUseListenerReadyFastRetry("runtime_unlock_offer_peer_response_timeout");
                    if (listenerReadyReuse)
                    {
                        LocalOperationalLog.Info(
                            "NKN.Tuna",
                            $"event=session_recovery_contract_listener_rearm_skipped; session_id={SanitizeLogToken(timedOutOffer.SessionId)}; reason=runtime_unlock_offer_peer_response_timeout; trigger=peer_response_timeout; payer_decision_id={timedOutOffer.PayerDecisionId}; listener_ready_reuse=1");
                    }
                    else
                    {
                        LocalOperationalLog.Warn(
                            "NKN.Tuna",
                            $"event=session_recovery_contract_listener_rearm_required; session_id={SanitizeLogToken(timedOutOffer.SessionId)}; reason=runtime_unlock_offer_peer_response_timeout; trigger=peer_response_timeout; payer_decision_id={timedOutOffer.PayerDecisionId}; listener_ready_reuse=0");
                        ScheduleAccelerationLaneStop("runtime_unlock_offer_peer_response_timeout");
                    }

                    if (recoveryRequested)
                    {
                        ArmRuntimeUnlockRetryAfterRecovery(
                            timedOutOffer.Generation,
                            recoverySessionId ?? timedOutOffer.SessionId,
                            "runtime_unlock_offer_peer_response_timeout",
                            recoveryReason ?? "tuna_activation_offer_peer_response_timeout",
                            requiresLocalListenerRetry: !listenerReadyReuse);
                    }
                    else
                    {
                        ScheduleAccelerationNegotiationRetry("runtime_unlock_offer_peer_response_timeout");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_runtime_unlock_offer_peer_response_timeout_failed; error={ex.GetType().Name}");
                }
            },
            CancellationToken.None);
    }

    private void ClearOutboundAccelerationOfferLocked()
    {
        outboundAccelerationOfferNonce = null;
        outboundAccelerationOfferTrigger = null;
        outboundAccelerationOfferPayerDecisionId = 0;
    }

    private void ClearRetiredOutboundAccelerationOfferLocked()
    {
        retiredAccelerationOfferSessionId = null;
        retiredAccelerationOfferNonce = null;
        retiredAccelerationOfferTrigger = null;
        retiredAccelerationOfferPayerDecisionId = 0;
        retiredAccelerationOfferExpiresUtcMs = 0;
    }

    private long GetPendingAccelerationAnswerAckGeneration()
    {
        lock (accelerationGate)
        {
            return pendingAccelerationAnswerAckGeneration;
        }
    }

    private bool IsPendingAccelerationAnswerAck(
        string? sessionId,
        string? nonce,
        long payerDecisionId,
        long generation)
    {
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? string.Empty : sessionId.Trim();
        var normalizedNonce = string.IsNullOrWhiteSpace(nonce) ? string.Empty : nonce.Trim();
        lock (accelerationGate)
        {
            return generation > 0 &&
                   pendingAccelerationAnswerAckGeneration == generation &&
                   string.Equals(pendingAccelerationAnswerAckSessionId, normalizedSessionId, StringComparison.Ordinal) &&
                   string.Equals(pendingAccelerationAnswerAckNonce, normalizedNonce, StringComparison.Ordinal) &&
                   pendingAccelerationAnswerAckPayerDecisionId == payerDecisionId;
        }
    }

    private void ClearPendingAccelerationAnswerAckLocked()
    {
        pendingAccelerationAnswerAckSessionId = null;
        pendingAccelerationAnswerAckNonce = null;
        pendingAccelerationAnswerAckTrigger = null;
        pendingAccelerationAnswerAckLanes = NknAccelerationLaneKind.None;
        pendingAccelerationAnswerAckPayerDecisionId = 0;
        pendingAccelerationAnswerAckGeneration++;
    }

    private bool RetirePendingAccelerationAnswerAckForRuntimeUnlockListenerRearm(
        string sessionId,
        string recoveryReason,
        string trigger)
    {
        string? pendingTrigger;
        long payerDecisionId;
        long generation;
        NknAccelerationLaneKind lanes;
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? string.Empty : sessionId.Trim();
        if (string.IsNullOrWhiteSpace(normalizedSessionId))
        {
            return false;
        }

        lock (accelerationGate)
        {
            if (string.IsNullOrWhiteSpace(pendingAccelerationAnswerAckNonce) ||
                !string.Equals(pendingAccelerationAnswerAckSessionId, normalizedSessionId, StringComparison.Ordinal))
            {
                return false;
            }

            pendingTrigger = pendingAccelerationAnswerAckTrigger;
            payerDecisionId = pendingAccelerationAnswerAckPayerDecisionId;
            generation = pendingAccelerationAnswerAckGeneration;
            lanes = pendingAccelerationAnswerAckLanes;
            ClearPendingAccelerationAnswerAckLocked();
        }

        LocalOperationalLog.Warn(
            "NKN.Tuna",
            $"event=tuna_acceleration_pending_answer_ack_retired_for_listener_rearm; session_id={SanitizeLogToken(normalizedSessionId)}; payer_decision_id={payerDecisionId}; generation={generation}; lanes={FormatAccelerationLanesForLog(lanes)}; pending_trigger={SanitizeLogToken(pendingTrigger)}; recovery_reason={SanitizeLogToken(recoveryReason)}; trigger={SanitizeLogToken(trigger)}");
        return true;
    }

    private TimeSpan ResolveAccelerationAnswerAckTimeout(
        string sessionId,
        string nonce,
        long payerDecisionId,
        long generation)
    {
        if (AccelerationAnswerAckTimeoutOverrideForTests is { } overrideTimeout)
        {
            return overrideTimeout;
        }

        lock (accelerationGate)
        {
            return IsPendingAccelerationAnswerAck(sessionId, nonce, payerDecisionId, generation) &&
                   IsRuntimeUnlockActivationReason(pendingAccelerationAnswerAckTrigger)
                ? RuntimeUnlockAccelerationAnswerAckTimeout
                : AccelerationAnswerAckTimeout;
        }
    }

    private void ScheduleAccelerationAnswerAckTimeout(
        string sessionId,
        string nonce,
        long payerDecisionId,
        long generation)
    {
        if (generation <= 0)
        {
            return;
        }

        var timeout = ResolveAccelerationAnswerAckTimeout(sessionId, nonce, payerDecisionId, generation);
        if (timeout <= TimeSpan.Zero)
        {
            return;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(timeout, CancellationToken.None).ConfigureAwait(false);
                    if (disposed ||
                        !IsPendingAccelerationAnswerAck(sessionId, nonce, payerDecisionId, generation))
                    {
                        return;
                    }

                    string? pendingTrigger;
                    lock (accelerationGate)
                    {
                        if (!IsPendingAccelerationAnswerAck(sessionId, nonce, payerDecisionId, generation))
                        {
                            return;
                        }

                        pendingTrigger = pendingAccelerationAnswerAckTrigger;
                        ClearPendingAccelerationAnswerAckLocked();
                    }

                    var timeoutReason = IsRuntimeUnlockActivationReason(pendingTrigger)
                        ? "runtime_unlock_answer_ack_timeout"
                        : "answer_ack_timeout";
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_answer_ack_timeout; session_id={SanitizeLogToken(sessionId)}; timeout_ms={(long)timeout.TotalMilliseconds}; payer_decision_id={payerDecisionId}; generation={generation}; trigger={SanitizeLogToken(pendingTrigger)}; retry_reason={SanitizeLogToken(timeoutReason)}");
                    ResumeFileTransferDataSessionsAfterTunaActivationNegotiation(
                        timeoutReason,
                        sessionId,
                        "answer_ack_timeout");
                    NotifyTransportAccelerationStateChanged(timeoutReason);
                    ScheduleAccelerationLaneStop(timeoutReason);
                    ScheduleAccelerationNegotiationRetry(timeoutReason);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_answer_ack_timeout_failed; error={ex.GetType().Name}");
                }
            },
            CancellationToken.None);
    }

    private async Task SendAccelerationAnswerAsync(
        TransportAccelerationOfferPayload offer,
        bool accepted,
        NknAccelerationLaneKind lanes,
        string? rejectReason,
        long pendingAnswerAckGeneration,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(remoteEndpoint) ||
            !TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            return;
        }

        var answer = new TransportAccelerationAnswerPayload
        {
            SessionId = offer.SessionId,
            Accepted = accepted,
            SupportedLanes = NknAccelerationLaneCodec.ToNames(lanes),
            ExpiresAtUnixMs = Math.Min(offer.ExpiresAtUnixMs, DateTimeOffset.UtcNow.Add(AccelerationOfferLifetime).ToUnixTimeMilliseconds()),
            Nonce = offer.Nonce,
            SidecarProtocolVersion = TunaSidecarProtocolVersion,
            RejectReason = accepted ? null : rejectReason,
            PayerDecisionId = offer.PayerDecisionId,
        };
        var payload = CreateSecureControlPayload(
            MsgType.TransportAccelerationAnswer,
            offer.Nonce,
            JsonSerializer.SerializeToUtf8Bytes(answer));
        var envelope = CreateEnvelope(envelopeCode, MsgType.TransportAccelerationAnswer, payload, replyTo: null);
        var requireRuntimeUnlockAnswerObservedSend =
            accepted &&
            pendingAnswerAckGeneration > 0 &&
            IsPendingAccelerationAnswerAckForSession(offer.SessionId);
        var queued = await SendAccelerationControlEnvelopeWithBulkBypassAsync(
                remoteEndpoint,
                envelope,
                "answer",
                ct,
                requireRuntimeUnlockAnswerObservedSend,
                offer.SessionId)
            .ConfigureAwait(false);
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_answer_{(queued.Succeeded ? "sent" : "rejected")}; accepted={(accepted ? 1 : 0)}; reason={SanitizeLogToken(rejectReason)}; lanes={string.Join(",", answer.SupportedLanes)}; payer_decision_id={answer.PayerDecisionId}");
        if (accepted &&
            pendingAnswerAckGeneration > 0 &&
            (queued.Succeeded || requireRuntimeUnlockAnswerObservedSend))
        {
            ScheduleAccelerationAnswerReplay(
                remoteEndpoint,
                envelope,
                answer.SessionId,
                answer.Nonce,
                answer.PayerDecisionId,
                pendingAnswerAckGeneration,
                ResolveAccelerationAnswerReplayAttempts(offer.Trigger, accepted),
                requireRuntimeUnlockAnswerObservedSend);
        }
    }

    private void ScheduleAccelerationAnswerReplay(
        string target,
        Envelope envelope,
        string sessionId,
        string nonce,
        long payerDecisionId,
        long generation,
        int maxAttempts = AccelerationAnswerReplayAttempts,
        bool requireObservedSend = false)
    {
        if (generation <= 0 ||
            maxAttempts <= 0)
        {
            return;
        }

        _ = Task.Run(
            async () =>
            {
                for (var attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    try
                    {
                        await Task.Delay(ResolveAccelerationAnswerReplayDelay(), CancellationToken.None).ConfigureAwait(false);
                        if (disposed ||
                            !IsPendingAccelerationAnswerAck(sessionId, nonce, payerDecisionId, generation))
                        {
                            return;
                        }

                        var requireReplayObservedSend =
                            requireObservedSend &&
                            IsPendingAccelerationAnswerAckForSession(sessionId);
                        var queued = await SendAccelerationControlEnvelopeWithBulkBypassAsync(
                                target,
                                envelope,
                                "answer_replay",
                                CancellationToken.None,
                                requireReplayObservedSend,
                                sessionId)
                            .ConfigureAwait(false);
                        LocalOperationalLog.Info(
                            "NKN.Tuna",
                            $"event=tuna_acceleration_answer_replay_{(queued.Succeeded ? "sent" : "rejected")}; attempt={attempt}; max_attempts={maxAttempts}; session_id={SanitizeLogToken(sessionId)}; payer_decision_id={payerDecisionId}; generation={generation}");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        LocalOperationalLog.Warn(
                            "NKN.Tuna",
                            $"event=tuna_acceleration_answer_replay_failed; attempt={attempt}; error={ex.GetType().Name}");
                    }
                }
            },
            CancellationToken.None);
    }

    private static TimeSpan ResolveAccelerationAnswerReplayDelay()
        => AccelerationAnswerReplayDelayOverrideForTests ?? AccelerationAnswerReplayDelay;

    private static int ResolveAccelerationAnswerReplayAttempts(string? trigger, bool accepted)
    {
        if (AccelerationAnswerReplayAttemptsOverrideForTests is { } overrideAttempts)
        {
            return Math.Max(0, overrideAttempts);
        }

        return accepted && IsRuntimeUnlockActivationReason(trigger)
            ? RuntimeUnlockAccelerationAnswerReplayAttempts
            : AccelerationAnswerReplayAttempts;
    }

    private async Task SendAccelerationAnswerAckAsync(
        TransportAccelerationAnswerPayload answer,
        NknAccelerationLaneKind lanes,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(remoteEndpoint) ||
            !TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            return;
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ack = new TransportAccelerationAnswerAckPayload
        {
            SessionId = answer.SessionId,
            Accepted = true,
            SupportedLanes = NknAccelerationLaneCodec.ToNames(lanes),
            SentAtUnixMs = nowMs,
            ExpiresAtUnixMs = Math.Min(answer.ExpiresAtUnixMs, DateTimeOffset.UtcNow.Add(AccelerationOfferLifetime).ToUnixTimeMilliseconds()),
            Nonce = answer.Nonce,
            SidecarProtocolVersion = TunaSidecarProtocolVersion,
            PayerDecisionId = answer.PayerDecisionId,
        };
        var payload = CreateSecureControlPayload(
            MsgType.TransportAccelerationAnswerAck,
            answer.Nonce,
            JsonSerializer.SerializeToUtf8Bytes(ack));
        var envelope = CreateEnvelope(envelopeCode, MsgType.TransportAccelerationAnswerAck, payload, replyTo: null);
        var queued = await SendAccelerationControlEnvelopeWithBulkBypassAsync(
                remoteEndpoint,
                envelope,
                "answer_ack",
                ct)
            .ConfigureAwait(false);
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_answer_ack_{(queued.Succeeded ? "sent" : "rejected")}; session_id={SanitizeLogToken(answer.SessionId)}; lanes={FormatAccelerationLanesForLog(lanes)}; payer_decision_id={answer.PayerDecisionId}");
        if (queued.Succeeded)
        {
            ScheduleAccelerationAnswerAckReplay(remoteEndpoint, envelope, answer.SessionId, answer.PayerDecisionId);
        }
    }

    private void ScheduleAccelerationAnswerAckReplay(
        string target,
        Envelope envelope,
        string sessionId,
        long payerDecisionId)
    {
        _ = Task.Run(
            async () =>
            {
                for (var attempt = 1; attempt <= AccelerationAnswerAckReplayAttempts; attempt++)
                {
                    try
                    {
                        await Task.Delay(AccelerationAnswerAckReplayDelay, CancellationToken.None).ConfigureAwait(false);
                        if (disposed || !IsAccelerationNegotiatedAndHealthy())
                        {
                            return;
                        }

                        var queued = await SendAccelerationControlEnvelopeWithBulkBypassAsync(
                                target,
                                envelope,
                                "answer_ack_replay",
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        LocalOperationalLog.Info(
                            "NKN.Tuna",
                            $"event=tuna_acceleration_answer_ack_replay_{(queued.Succeeded ? "sent" : "rejected")}; attempt={attempt}; session_id={SanitizeLogToken(sessionId)}; payer_decision_id={payerDecisionId}");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        LocalOperationalLog.Warn(
                            "NKN.Tuna",
                            $"event=tuna_acceleration_answer_ack_replay_failed; attempt={attempt}; error={ex.GetType().Name}");
                    }
                }
            },
            CancellationToken.None);
    }

    private async Task<AccelerationControlSendResult> SendAccelerationControlEnvelopeWithBulkBypassAsync(
        string target,
        Envelope envelope,
        string purpose,
        CancellationToken ct,
        bool requireObservedSend = false,
        string? activationSessionId = null,
        Action? onBeforeSend = null,
        Action? onQueueAccepted = null,
        Action? onObservedSendWaitStarted = null,
        Func<string?>? queueAcceptedAsObservedReason = null)
    {
        onBeforeSend?.Invoke();

        if (!await WaitForFileTransferTunaActivationBridgeRecoveryBeforeControlSendAsync(
                purpose,
                activationSessionId,
                ct)
            .ConfigureAwait(false))
        {
            if (requireObservedSend &&
                TryRequestFileTransferTunaActivationOfferSendRecovery(
                    purpose,
                    activationSessionId,
                    "bridge_recovery_wait_timeout",
                    out var recoveryReason,
                    out var recoverySessionId))
            {
                return AccelerationControlSendResult.RecoveryRequestedResult(
                    recoveryReason ?? "tuna_activation_offer_send_timeout",
                    recoverySessionId);
            }

            return AccelerationControlSendResult.Failed;
        }

        var queueObservedReason = requireObservedSend
            ? queueAcceptedAsObservedReason?.Invoke()
            : null;
        if (requireObservedSend &&
            IsTunaActivationOfferSendPurpose(purpose) &&
            TryGetRuntimeUnlockRetryAuthorityForCurrentOffer(out _))
        {
            MarkRuntimeUnlockRecoveryContractAuthoritySendStarted(purpose);
        }

        Func<string?>? bulkQueueFallbackSkipReason = requireObservedSend
            ? () => GetRuntimeUnlockBulkQueueFallbackObservedProofSkipReason(activationSessionId, queueObservedReason)
            : null;
        Func<string?>? bulkQueueFallbackAfterDirectSuccessReason = requireObservedSend &&
            IsRuntimeUnlockPeerVisibleActivationSendPurpose(purpose, activationSessionId)
            ? () => GetRuntimeUnlockBulkQueueFallbackAfterDirectSuccessReason(purpose, activationSessionId)
            : null;
        Func<string?>? bulkQueueFallbackObservedProofFailureReason = requireObservedSend &&
            IsTunaActivationOfferSendPurpose(purpose)
            ? () => GetRuntimeUnlockBulkQueueFallbackObservedProofFailureReason(activationSessionId, purpose)
            : null;

        var bytes = EnvelopeCodec.Serialize(envelope);
        var controlTask = QueueControlEnvelopeAsync(target, envelope, ControlOutboundLane.High, ct);
        ObserveAccelerationControlSendTask(controlTask, purpose, "control_queue");

        var priorityControlTask = SendAccelerationControlPriorityCopyAsync(target, envelope, bytes, purpose, ct);
        ObserveAccelerationControlSendTask(priorityControlTask, purpose, "control_priority");

        Task<AccelerationControlSendResult>? bulkTask = null;
        if (string.IsNullOrWhiteSpace(remoteBulkEndpoint))
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_acceleration_control_bulk_bypass_unavailable; purpose={SanitizeLogToken(purpose)}; message_type={MapAccelerationControlMessageType(envelope.Type)}; reason=missing_remote_bulk_endpoint");
        }
        else
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_control_bulk_bypass_started; purpose={SanitizeLogToken(purpose)}; message_type={MapAccelerationControlMessageType(envelope.Type)}; delay_ms=0; mode=control_to_bulk_endpoint");
            bulkTask = SendAccelerationControlBulkCopyAsync(
                remoteBulkEndpoint,
                envelope,
                bytes,
                purpose,
                ct,
                bulkQueueFallbackSkipReason,
                bulkQueueFallbackAfterDirectSuccessReason,
                bulkQueueFallbackObservedProofFailureReason);
            ObserveAccelerationControlSendTask(bulkTask, purpose, "control_to_bulk_endpoint");
        }

        if (IsAccelerationControlQueueAccepted(controlTask, ct, out var queueRejected))
        {
            var queueCompletedSuccessfully = controlTask.IsCompletedSuccessfully && controlTask.Result;
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_control_queue_accepted; purpose={SanitizeLogToken(purpose)}; message_type={MapAccelerationControlMessageType(envelope.Type)}; completed={(controlTask.IsCompletedSuccessfully ? 1 : 0)}");
            if (queueCompletedSuccessfully)
            {
                onQueueAccepted?.Invoke();
            }

            if (!string.IsNullOrWhiteSpace(queueObservedReason) && queueCompletedSuccessfully)
            {
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_control_queue_accepted_as_observed; purpose={SanitizeLogToken(purpose)}; message_type={MapAccelerationControlMessageType(envelope.Type)}; reason={SanitizeLogToken(queueObservedReason)}");
                return AccelerationControlSendResult.Success(AccelerationObservedLaneControlQueueExplicitObserved);
            }

            if (!string.IsNullOrWhiteSpace(queueObservedReason) && !queueCompletedSuccessfully)
            {
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_control_queue_pending_not_observed; purpose={SanitizeLogToken(purpose)}; message_type={MapAccelerationControlMessageType(envelope.Type)}; reason={SanitizeLogToken(queueObservedReason)}");
            }

            if (!requireObservedSend)
            {
                return AccelerationControlSendResult.Success(AccelerationObservedLaneControlQueue);
            }
        }
        else if (queueRejected)
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_acceleration_control_queue_rejected; purpose={SanitizeLogToken(purpose)}; message_type={MapAccelerationControlMessageType(envelope.Type)}");
        }

        var preferPriorityObservedLane = ShouldPreferPriorityObservedLaneForRuntimeUnlockObservedOfferReplay(
            requireObservedSend,
            purpose);
        var preferBulkObservedLane =
            !preferPriorityObservedLane &&
            ShouldPreferBulkObservedLaneForRuntimeUnlockAuthority(
                requireObservedSend,
                purpose,
                activationSessionId);

        var attempts = new List<AccelerationControlSendAttempt>
        {
            new(
                MapAccelerationControlSendAttemptAsync(priorityControlTask, AccelerationObservedLaneControlPriority),
                preferPriorityObservedLane),
        };
        if (!requireObservedSend)
        {
            attempts.Add(new AccelerationControlSendAttempt(MapAccelerationControlSendAttemptAsync(controlTask, AccelerationObservedLaneControlQueue)));
        }
        else if (!string.IsNullOrWhiteSpace(queueObservedReason))
        {
            attempts.Add(new AccelerationControlSendAttempt(MapAccelerationControlQueueAcceptedObservedAttemptAsync(
                controlTask,
                purpose,
                envelope.Type,
                queueObservedReason)));
        }
        else
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_control_queue_excluded_from_observed_wait; purpose={SanitizeLogToken(purpose)}; message_type={MapAccelerationControlMessageType(envelope.Type)}; reason=observed_send_requires_direct_or_bulk_proof");
        }

        if (bulkTask is not null)
        {
            attempts.Add(new AccelerationControlSendAttempt(bulkTask, preferBulkObservedLane));
        }

        var observedSend = await WaitForFirstSuccessfulAccelerationControlSendAsync(
                attempts,
                purpose,
                preferPriorityObservedLane || preferBulkObservedLane)
            .ConfigureAwait(false);
        if (observedSend.Succeeded && requireObservedSend)
        {
            var priorityReplayTrusted = ShouldTrustPriorityObservedLaneForRuntimeUnlockObservedOfferReplay(
                purpose,
                observedSend.ObservedLane);
            if (priorityReplayTrusted)
            {
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_control_priority_offer_replay_observed_trusted; purpose={SanitizeLogToken(purpose)}; message_type={MapAccelerationControlMessageType(envelope.Type)}; observed_lane={SanitizeLogToken(observedSend.ObservedLane ?? "(none)")}; session_id={SanitizeLogToken(activationSessionId ?? "none")}; reason=runtime_unlock_observed_offer_replay_lane_diversity");
            }

            var hasRuntimeUnlockRetryAuthority =
                TryGetRuntimeUnlockRetryAuthorityForCurrentOffer(out _);
            var hasRuntimeUnlockObservedReplayWindow =
                string.Equals(purpose, "offer_replay", StringComparison.OrdinalIgnoreCase) &&
                TryGetRuntimeUnlockObservedOfferReplayWindowForCurrentOffer(out _, out _);
            if (IsTunaActivationOfferSendPurpose(purpose) &&
                IsCurrentRuntimeUnlockActivationOffer() &&
                (hasRuntimeUnlockRetryAuthority || hasRuntimeUnlockObservedReplayWindow) &&
                !HasRecentSessionLivenessProofForRuntimeUnlockAuthority(activationSessionId))
            {
                const string missingPeerProofReason = "runtime_unlock_authority_missing_recent_peer_proof";
                var retryAuthorityObservedSendTrusted =
                    ShouldTrustRuntimeUnlockObservedSendWithRetryAuthority(
                        activationSessionId,
                        purpose,
                        observedSend.ObservedLane,
                        out var retryAuthorityContractGeneration,
                        out var retryAuthorityAttempt);
                var fallbackRepairProofTrusted =
                    ShouldTrustRuntimeUnlockObservedSendWithPostTunaFallbackRepairProof(
                        activationSessionId,
                        observedSend.ObservedLane,
                        out var fallbackProof,
                        out var fallbackProofDirection,
                        out var fallbackProofAgeMs);
                if (retryAuthorityObservedSendTrusted)
                {
                    LocalOperationalLog.Info(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_control_observed_trusted_by_runtime_unlock_authority; purpose={SanitizeLogToken(purpose)}; message_type={MapAccelerationControlMessageType(envelope.Type)}; observed_lane={SanitizeLogToken(observedSend.ObservedLane ?? "(none)")}; session_id={SanitizeLogToken(activationSessionId ?? "none")}; contract_generation={retryAuthorityContractGeneration}; authority_attempt={retryAuthorityAttempt}; reason=bounded_authority_peer_visible_lane");
                }
                else if (fallbackRepairProofTrusted)
                {
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_control_send_observed_without_recent_peer_proof; purpose={SanitizeLogToken(purpose)}; message_type={MapAccelerationControlMessageType(envelope.Type)}; observed_lane={SanitizeLogToken(observedSend.ObservedLane ?? "(none)")}; session_id={SanitizeLogToken(activationSessionId ?? "none")}; reason={missingPeerProofReason}; fallback_repair_proof_trusted=1; fallback_proof={SanitizeLogToken(fallbackProof)}; fallback_proof_direction={SanitizeLogToken(fallbackProofDirection)}; fallback_proof_age_ms={fallbackProofAgeMs}");
                    LocalOperationalLog.Info(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_control_observed_trusted_by_post_tuna_fallback_repair; purpose={SanitizeLogToken(purpose)}; message_type={MapAccelerationControlMessageType(envelope.Type)}; observed_lane={SanitizeLogToken(observedSend.ObservedLane ?? "(none)")}; session_id={SanitizeLogToken(activationSessionId ?? "none")}; proof={SanitizeLogToken(fallbackProof)}; proof_direction={SanitizeLogToken(fallbackProofDirection)}; proof_age_ms={fallbackProofAgeMs}");
                }
                else if (!priorityReplayTrusted)
                {
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_control_send_observed_without_recent_peer_proof; purpose={SanitizeLogToken(purpose)}; message_type={MapAccelerationControlMessageType(envelope.Type)}; observed_lane={SanitizeLogToken(observedSend.ObservedLane ?? "(none)")}; session_id={SanitizeLogToken(activationSessionId ?? "none")}; reason={missingPeerProofReason}");
                    if (string.Equals(
                            observedSend.ObservedLane,
                            AccelerationObservedLaneControlPriority,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        LocalOperationalLog.Warn(
                            "NKN.Tuna",
                            $"event=tuna_acceleration_control_priority_observed_untrusted; purpose={SanitizeLogToken(purpose)}; message_type={MapAccelerationControlMessageType(envelope.Type)}; observed_lane={SanitizeLogToken(observedSend.ObservedLane)}; session_id={SanitizeLogToken(activationSessionId ?? "none")}; reason={missingPeerProofReason}; preferred_bulk_required=1");
                    }
                    else
                    {
                        LocalOperationalLog.Warn(
                            "NKN.Tuna",
                            $"event=tuna_acceleration_control_observed_untrusted; purpose={SanitizeLogToken(purpose)}; message_type={MapAccelerationControlMessageType(envelope.Type)}; observed_lane={SanitizeLogToken(observedSend.ObservedLane ?? "(none)")}; session_id={SanitizeLogToken(activationSessionId ?? "none")}; reason={missingPeerProofReason}; peer_visible_proof_required=1");
                    }

                    if (TryRequestFileTransferTunaActivationOfferSendRecovery(
                            purpose,
                            activationSessionId,
                            missingPeerProofReason,
                            out var recoveryReason,
                            out var recoverySessionId))
                    {
                        return AccelerationControlSendResult.RecoveryRequestedResult(
                            recoveryReason ?? "tuna_activation_offer_send_timeout",
                            recoverySessionId);
                    }

                    MarkRuntimeUnlockRecoveryContractAuthorityBlocked(missingPeerProofReason);
                    return AccelerationControlSendResult.Failed;
                }
            }

            onObservedSendWaitStarted?.Invoke();
        }
        else if (!observedSend.Succeeded && requireObservedSend)
        {
            if (TryRequestFileTransferTunaActivationOfferSendRecovery(
                purpose,
                activationSessionId,
                "observed_send_timeout",
                out var recoveryReason,
                out var recoverySessionId))
            {
                return AccelerationControlSendResult.RecoveryRequestedResult(
                    recoveryReason ?? "tuna_activation_offer_send_timeout",
                    recoverySessionId);
            }
        }

        return observedSend;
    }

    private bool ShouldTrustRuntimeUnlockObservedSendWithRetryAuthority(
        string? sessionId,
        string purpose,
        string? observedLane,
        out long contractGeneration,
        out int authorityAttempt)
    {
        contractGeneration = 0;
        authorityAttempt = 0;
        if (string.IsNullOrWhiteSpace(sessionId) ||
            string.IsNullOrWhiteSpace(observedLane) ||
            !IsCurrentRuntimeUnlockActivationOffer())
        {
            return false;
        }

        var normalizedLane = observedLane.Trim();
        var isPriorityLane = string.Equals(normalizedLane, AccelerationObservedLaneControlPriority, StringComparison.OrdinalIgnoreCase);
        var isBulkEndpointLane = string.Equals(normalizedLane, AccelerationObservedLaneControlToBulkEndpoint, StringComparison.OrdinalIgnoreCase);
        var isBulkFallbackLane = string.Equals(normalizedLane, AccelerationObservedLaneBulkQueueFallback, StringComparison.OrdinalIgnoreCase);
        if (!isPriorityLane && !isBulkEndpointLane && !isBulkFallbackLane)
        {
            return false;
        }

        if (!TryGetRuntimeUnlockRetryAuthorityForCurrentOffer(out var authorityState) ||
            authorityState is null ||
            !string.Equals(authorityState.SessionId, sessionId.Trim(), StringComparison.Ordinal))
        {
            if (!string.Equals(purpose, "offer_replay", StringComparison.OrdinalIgnoreCase) ||
                isPriorityLane ||
                !TryGetRuntimeUnlockObservedOfferReplayWindowForCurrentOffer(out var replayState, out _) ||
                replayState is null ||
                !string.Equals(replayState.SessionId, sessionId.Trim(), StringComparison.Ordinal))
            {
                return false;
            }

            contractGeneration = replayState.ContractGeneration;
            authorityAttempt = replayState.AuthorityAttempt;
            return true;
        }

        if (isPriorityLane)
        {
            return false;
        }

        contractGeneration = authorityState.ContractGeneration;
        authorityAttempt = authorityState.AuthorityAttempt;
        return true;
    }

    private bool ShouldPreferBulkObservedLaneForRuntimeUnlockAuthority(
        bool requireObservedSend,
        string purpose,
        string? sessionId)
    {
        if (!requireObservedSend)
        {
            return false;
        }

        if (IsTunaActivationAnswerSendPurpose(purpose))
        {
            return IsPendingAccelerationAnswerAckForSession(sessionId);
        }

        if (!IsTunaActivationOfferSendPurpose(purpose))
        {
            return false;
        }

        if (TryGetRuntimeUnlockRetryAuthorityForCurrentOffer(out _) ||
            (string.Equals(purpose, "offer_replay", StringComparison.OrdinalIgnoreCase) &&
             TryGetRuntimeUnlockObservedOfferReplayWindowForCurrentOffer(out _, out _)))
        {
            return true;
        }

        return IsRuntimeUnlockPeerVisibleActivationSendPurpose(purpose, sessionId) ||
               ShouldRequireRuntimeUnlockBulkQueueDuplicateForPostTunaFallback(
                   sessionId,
                   requireCurrentRuntimeUnlockOffer: false);
    }

    private bool ShouldPreferPriorityObservedLaneForRuntimeUnlockObservedOfferReplay(
        bool requireObservedSend,
        string purpose)
        => false;

    private bool ShouldTrustPriorityObservedLaneForRuntimeUnlockObservedOfferReplay(
        string purpose,
        string? observedLane)
        => false;

    private bool ShouldTrustRuntimeUnlockObservedSendWithPostTunaFallbackRepairProof(
        string? sessionId,
        string? observedLane,
        out string proof,
        out string proofDirection,
        out long proofAgeMs)
    {
        proof = "none";
        proofDirection = "none";
        proofAgeMs = 0;
        if (string.IsNullOrWhiteSpace(sessionId) ||
            string.IsNullOrWhiteSpace(observedLane) ||
            !IsCurrentRuntimeUnlockActivationOffer())
        {
            return false;
        }

        var normalizedSessionId = sessionId.Trim();
        if (!HasActivePostTunaFallbackFileTransferRouteHint(normalizedSessionId))
        {
            return false;
        }

        var normalizedLane = observedLane.Trim();
        if (!string.Equals(normalizedLane, AccelerationObservedLaneControlToBulkEndpoint, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(normalizedLane, AccelerationObservedLaneBulkQueueFallback, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return TryGetCurrentPostTunaFallbackObservedSendProbeProof(
            normalizedSessionId,
            out proof,
            out proofDirection,
            out proofAgeMs);
    }

    private string? GetRuntimeUnlockBulkQueueFallbackObservedProofSkipReason(string? sessionId, string? queueObservedReason)
    {
        if (!IsCurrentRuntimeUnlockActivationOffer())
        {
            return null;
        }

        if (TryGetRuntimeUnlockObservedOfferReplayWindowForCurrentOffer(out _, out _))
        {
            return null;
        }

        if (ShouldTrustRuntimeUnlockObservedSendWithPostTunaFallbackRepairProof(
                sessionId,
                AccelerationObservedLaneBulkQueueFallback,
                out var proof,
                out var proofDirection,
                out var proofAgeMs))
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_control_bulk_queue_fallback_trusted_by_post_tuna_fallback_repair; purpose=offer; message_type={MapAccelerationControlMessageType(MsgType.TransportAccelerationOffer)}; lane=bulk_queue_fallback; session_id={SanitizeLogToken(sessionId ?? "none")}; proof={SanitizeLogToken(proof)}; proof_direction={SanitizeLogToken(proofDirection)}; proof_age_ms={proofAgeMs}; reason=current_post_tuna_fallback_repair_proof");
            return null;
        }

        if (RuntimeUnlockOfferObservedSendBlockerReasonOverrideForTests?.Invoke(this) is { Length: > 0 } overrideReason)
        {
            if (TryGetRuntimeUnlockRetryAuthorityForCurrentOffer(out _))
            {
                return null;
            }

            MarkRuntimeUnlockRecoveryContractAuthorityBlocked(overrideReason);
            return SanitizeLogToken(overrideReason);
        }

        if (IsRuntimeUnlockActiveFileTransferObservedSendBlocker(queueObservedReason) ||
            string.Equals(queueObservedReason, "test_regular_v4_pressure", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetRuntimeUnlockRetryAuthorityForCurrentOffer(out _))
            {
                return null;
            }

            MarkRuntimeUnlockRecoveryContractAuthorityBlocked(queueObservedReason);
            return SanitizeLogToken(queueObservedReason);
        }

        if (client is not RealNknClientAdapter realClient)
        {
            return null;
        }

        if (realClient.TryGetFileTransferRegularV4ActivationSendBlocker(
                out var blockerReason,
                out _,
                includeRegularV4Pressure: true) &&
            IsRuntimeUnlockActiveFileTransferObservedSendBlocker(blockerReason))
        {
            if (TryGetRuntimeUnlockRetryAuthorityForCurrentOffer(out _))
            {
                return null;
            }

            MarkRuntimeUnlockRecoveryContractAuthorityBlocked(blockerReason);
            return SanitizeLogToken(blockerReason);
        }

        if (realClient.HasActiveFileTransferRuntimeForActivationSend())
        {
            if (TryGetRuntimeUnlockRetryAuthorityForCurrentOffer(out _))
            {
                return null;
            }

            MarkRuntimeUnlockRecoveryContractAuthorityBlocked("active_file_transfer_runtime");
            return "active_file_transfer_runtime";
        }

        return null;
    }

    private string? GetRuntimeUnlockBulkQueueFallbackAfterDirectSuccessReason(string purpose, string? sessionId)
        => IsTunaActivationAnswerSendPurpose(purpose) &&
            IsPendingAccelerationAnswerAckForSession(sessionId)
            ? IsPendingRuntimeUnlockAccelerationAnswerAck(sessionId)
                ? "runtime_unlock_answer_requires_bulk_queue_duplicate"
                : "answer_requires_bulk_queue_duplicate"
            : IsTunaActivationOfferSendPurpose(purpose) &&
              ShouldRequireRuntimeUnlockBulkQueueDuplicateForPostTunaFallback(sessionId, requireCurrentRuntimeUnlockOffer: false)
            ? "post_tuna_fallback_requires_bulk_queue_duplicate"
            : null;

    private bool ShouldRequireRuntimeUnlockBulkQueueDuplicateForPostTunaFallback(
        string? sessionId,
        bool requireCurrentRuntimeUnlockOffer = true)
    {
        if ((requireCurrentRuntimeUnlockOffer && !IsCurrentRuntimeUnlockActivationOffer()) ||
            string.IsNullOrWhiteSpace(sessionId) ||
            HasRecentSessionLivenessProofForRuntimeUnlockAuthority(sessionId) ||
            !HasActivePostTunaFallbackFileTransferRouteHint(sessionId))
        {
            return false;
        }

        return TryGetCurrentPostTunaFallbackObservedSendProbeProof(
            sessionId.Trim(),
            out _,
            out _,
            out _);
    }

    private string? GetRuntimeUnlockBulkQueueFallbackObservedProofFailureReason(string? sessionId, string purpose)
    {
        if (!IsCurrentRuntimeUnlockActivationOffer() ||
            HasRecentSessionLivenessProofForRuntimeUnlockAuthority(sessionId))
        {
            return null;
        }

        if (ShouldTrustRuntimeUnlockObservedSendWithPostTunaFallbackRepairProof(
                sessionId,
                AccelerationObservedLaneBulkQueueFallback,
                out _,
                out _,
                out _))
        {
            return null;
        }

        if (client is RealNknClientAdapter readinessClient &&
            readinessClient.TryGetRuntimeUnlockBulkQueueObservedProofBlocker(out var readinessReason) &&
            IsRuntimeUnlockBridgeConnectivityObservedProofBlocker(readinessReason))
        {
            return readinessReason;
        }

        if (TryGetRuntimeUnlockRetryAuthorityForCurrentOffer(out var authorityState))
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_control_bulk_queue_fallback_trusted_by_runtime_unlock_authority; purpose=offer; message_type={MapAccelerationControlMessageType(MsgType.TransportAccelerationOffer)}; lane=bulk_queue_fallback; session_id={SanitizeLogToken(sessionId ?? "none")}; contract_generation={authorityState?.ContractGeneration ?? 0}; authority_attempt={authorityState?.AuthorityAttempt ?? 0}; reason=bounded_authority_peer_visible_lane");
            return null;
        }

        if (string.Equals(purpose, "offer_replay", StringComparison.OrdinalIgnoreCase) &&
            TryGetRuntimeUnlockObservedOfferReplayWindowForCurrentOffer(out var replayState, out var offerState) &&
            replayState is not null &&
            offerState is not null &&
            (string.IsNullOrWhiteSpace(sessionId) ||
             string.Equals(replayState.SessionId, sessionId.Trim(), StringComparison.Ordinal)))
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_control_bulk_observed_send_allowed_by_runtime_unlock_observed_replay; purpose=offer_replay; message_type={MapAccelerationControlMessageType(MsgType.TransportAccelerationOffer)}; lane=bulk_observed; session_id={SanitizeLogToken(sessionId ?? "none")}; contract_generation={replayState.ContractGeneration}; authority_attempt={replayState.AuthorityAttempt}; offer_generation={offerState.Generation}; previous_observed_lane={SanitizeLogToken(offerState.ObservedSendLane ?? "(none)")}; reason=bounded_observed_offer_replay");
            return null;
        }

        if (client is RealNknClientAdapter realClient &&
            realClient.TryGetRuntimeUnlockBulkQueueObservedProofBlocker(out var reason) &&
            !string.IsNullOrWhiteSpace(reason))
        {
            return reason;
        }

        const string missingPeerProofReason = "runtime_unlock_authority_missing_recent_peer_proof";
        return missingPeerProofReason;
    }

    private static bool IsRuntimeUnlockBridgeConnectivityObservedProofBlocker(string? reason)
        => string.Equals(reason, "bridge_health_missing", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(reason, "bridge_health_stale", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(reason, "bridge_disconnect_signal", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(reason, "bridge_not_ready", StringComparison.OrdinalIgnoreCase);

    private static bool IsRuntimeUnlockActiveFileTransferObservedSendBlocker(string? reason)
    {
        if (IsRegularV4PressureActivationSendBlocker(reason) ||
            IsReceiveStallActivationSendBlocker(reason))
        {
            return true;
        }

        return string.Equals(reason, "all_zero_receive_window", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(reason, "control_zero_receive_window", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(reason, "bulk_zero_receive_window", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(reason, "regular_v4_receive_stall_bypass", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(reason, "recent_zero_receive_health", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAccelerationControlQueueAccepted(
        Task<bool> controlTask,
        CancellationToken ct,
        out bool rejected)
    {
        rejected = false;
        if (!controlTask.IsCompleted)
        {
            return true;
        }

        if (controlTask.IsCanceled)
        {
            ct.ThrowIfCancellationRequested();
            return false;
        }

        if (controlTask.IsFaulted)
        {
            return false;
        }

        if (controlTask.Result)
        {
            return true;
        }

        rejected = true;
        return false;
    }

    private static async Task<AccelerationControlSendResult> MapAccelerationControlSendAttemptAsync(
        Task<bool> attempt,
        string lane)
        => await attempt.ConfigureAwait(false)
            ? AccelerationControlSendResult.Success(lane)
            : AccelerationControlSendResult.Failed;

    private static async Task<AccelerationControlSendResult> MapAccelerationControlQueueAcceptedObservedAttemptAsync(
        Task<bool> attempt,
        string purpose,
        MsgType messageType,
        string queueObservedReason)
    {
        if (!await attempt.ConfigureAwait(false))
        {
            return AccelerationControlSendResult.Failed;
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_control_queue_accepted_as_observed; purpose={SanitizeLogToken(purpose)}; message_type={MapAccelerationControlMessageType(messageType)}; reason={SanitizeLogToken(queueObservedReason)}");
        return AccelerationControlSendResult.Success(AccelerationObservedLaneControlQueueExplicitObserved);
    }

    private static async Task<AccelerationControlSendResult> WaitForFirstSuccessfulAccelerationControlSendAsync(
        List<AccelerationControlSendAttempt> attempts,
        string purpose,
        bool preferPreferredObservedLane = false)
    {
        if (attempts.Count == 0)
        {
            return AccelerationControlSendResult.Failed;
        }

        var waitTimeout = ResolveAccelerationControlBulkBypassWait();
        var remaining = new List<AccelerationControlSendAttempt>(attempts);
        AccelerationControlSendResult? fallbackObservedSend = null;
        var waitingForPreferredLaneLogged = false;
        var timeoutTask = Task.Delay(waitTimeout);
        while (remaining.Count > 0)
        {
            var completed = await Task.WhenAny(remaining.Select(static attempt => (Task)attempt.Task).Append(timeoutTask)).ConfigureAwait(false);
            if (ReferenceEquals(completed, timeoutTask))
            {
                if (fallbackObservedSend is { Succeeded: true })
                {
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_control_send_preferred_bulk_observed_lane_unavailable; purpose={SanitizeLogToken(purpose)}; fallback_lane={SanitizeLogToken(fallbackObservedSend.Value.ObservedLane ?? "(none)")}; reason=timeout; wait_ms={(long)waitTimeout.TotalMilliseconds}; remaining={remaining.Count}");
                    return fallbackObservedSend.Value;
                }

                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_control_send_wait_timeout; purpose={SanitizeLogToken(purpose)}; wait_ms={(long)waitTimeout.TotalMilliseconds}; remaining={remaining.Count}");
                return AccelerationControlSendResult.Failed;
            }

            var completedAttempt = remaining.First(attempt => ReferenceEquals(attempt.Task, completed));
            remaining.Remove(completedAttempt);
            try
            {
                var result = await completedAttempt.Task.ConfigureAwait(false);
                if (result.Succeeded)
                {
                    if (preferPreferredObservedLane && !completedAttempt.PreferredObservedLane)
                    {
                        fallbackObservedSend ??= result;
                        if (remaining.Any(static attempt => attempt.PreferredObservedLane))
                        {
                            if (!waitingForPreferredLaneLogged)
                            {
                                waitingForPreferredLaneLogged = true;
                                LocalOperationalLog.Info(
                                    "NKN.Tuna",
                                    $"event=tuna_acceleration_control_send_waiting_for_preferred_bulk_observed_lane; purpose={SanitizeLogToken(purpose)}; fallback_lane={SanitizeLogToken(result.ObservedLane ?? "(none)")}; wait_ms={(long)waitTimeout.TotalMilliseconds}");
                            }

                            continue;
                        }

                        LocalOperationalLog.Warn(
                            "NKN.Tuna",
                            $"event=tuna_acceleration_control_send_preferred_bulk_observed_lane_unavailable; purpose={SanitizeLogToken(purpose)}; fallback_lane={SanitizeLogToken(result.ObservedLane ?? "(none)")}; reason=preferred_attempts_completed");
                        return result;
                    }

                    if (preferPreferredObservedLane && completedAttempt.PreferredObservedLane)
                    {
                        LocalOperationalLog.Info(
                            "NKN.Tuna",
                            $"event=tuna_acceleration_control_send_preferred_bulk_observed_lane_selected; purpose={SanitizeLogToken(purpose)}; observed_lane={SanitizeLogToken(result.ObservedLane ?? "(none)")}");
                    }

                    return result;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_control_send_attempt_failed; purpose={SanitizeLogToken(purpose)}; error={ex.GetType().Name}");
            }

            if (preferPreferredObservedLane &&
                fallbackObservedSend is { Succeeded: true } &&
                !remaining.Any(static attempt => attempt.PreferredObservedLane))
            {
                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_control_send_preferred_bulk_observed_lane_unavailable; purpose={SanitizeLogToken(purpose)}; fallback_lane={SanitizeLogToken(fallbackObservedSend.Value.ObservedLane ?? "(none)")}; reason=preferred_attempts_failed");
                return fallbackObservedSend.Value;
            }
        }

        if (fallbackObservedSend is { Succeeded: true })
        {
            return fallbackObservedSend.Value;
        }

        return AccelerationControlSendResult.Failed;
    }

    private void ScheduleRuntimeUnlockPeerVisibleOfferDeliveryConfirmation(
        string target,
        Envelope envelope,
        string sessionId,
        long payerDecisionId,
        string nonce,
        long offerGeneration,
        string trigger)
    {
        _ = Task.Run(
            () => TryConfirmRuntimeUnlockPeerVisibleOfferDeliveryAsync(
                target,
                envelope,
                sessionId,
                payerDecisionId,
                nonce,
                offerGeneration,
                trigger,
                CancellationToken.None),
            CancellationToken.None);
    }

    private async Task<bool> TryConfirmRuntimeUnlockPeerVisibleOfferDeliveryAsync(
        string target,
        Envelope envelope,
        string sessionId,
        long payerDecisionId,
        string nonce,
        long offerGeneration,
        string trigger,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(target) ||
            string.IsNullOrWhiteSpace(sessionId) ||
            string.IsNullOrWhiteSpace(nonce) ||
            offerGeneration <= 0 ||
            envelope.Type != MsgType.TransportAccelerationOffer)
        {
            return false;
        }

        LifecycleDeliveryResult result;
        try
        {
            result = await SendLifecycleEnvelopeAsync(
                    new LifecycleDeliveryOptions(
                        MessageType: MsgType.TransportAccelerationOffer,
                        RequestId: nonce,
                        ControlDestination: target,
                        BulkDestination: remoteBulkEndpoint,
                        LogCategory: "NKN.Tuna",
                        LogEvent: "runtime_unlock_control_plane_delivery",
                        AllowBulkDuplicate: true,
                        IgnoreCallerCancellation: false,
                        AcceptancePolicy: LifecycleDeliveryAcceptancePolicy.PeerVisibleRequired,
                        UseControlAckRetry: true,
                        PeerCopyAttempts: 2,
                        ThrowOnFailure: false,
                        CopyTimeout: RuntimeUnlockPeerVisibleLifecycleCopyTimeout),
                    envelope,
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=runtime_unlock_control_plane_delivery_failed; session_id={SanitizeLogToken(sessionId)}; payer_decision_id={payerDecisionId}; generation={offerGeneration}; trigger={SanitizeLogToken(trigger)}; error={ex.GetType().Name}");
            return false;
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=runtime_unlock_control_plane_delivery_result; session_id={SanitizeLogToken(sessionId)}; payer_decision_id={payerDecisionId}; generation={offerGeneration}; trigger={SanitizeLogToken(trigger)}; control_ack={(result.ControlAck ? 1 : 0)}; control_copy={(result.ControlCopy ? 1 : 0)}; bulk_copy={(result.BulkCopy ? 1 : 0)}; peer_visible_any={(result.PeerVisibleAny ? 1 : 0)}; accepted_any={(result.AcceptedAny ? 1 : 0)}; control_ack_error={result.ControlAckErrorName}; control_copy_error={result.ControlCopyErrorName}; bulk_copy_error={result.BulkCopyErrorName}");

        if (!result.ControlAck)
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=runtime_unlock_control_plane_ack_not_observed; session_id={SanitizeLogToken(sessionId)}; payer_decision_id={payerDecisionId}; generation={offerGeneration}; trigger={SanitizeLogToken(trigger)}; control_copy={(result.ControlCopy ? 1 : 0)}; bulk_copy={(result.BulkCopy ? 1 : 0)}; reason=offer_ack_not_received");
            return false;
        }

        if (!MarkRuntimeUnlockCutThroughPeerReceived(
                sessionId,
                payerDecisionId,
                nonce,
                "transport_acceleration_offer_control_ack"))
        {
            MarkRuntimeUnlockPeerReceivedIfCurrent(
                sessionId,
                payerDecisionId,
                nonce,
                offerGeneration,
                "transport_acceleration_offer_control_ack");
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=runtime_unlock_control_plane_peer_received; session_id={SanitizeLogToken(sessionId)}; payer_decision_id={payerDecisionId}; generation={offerGeneration}; trigger={SanitizeLogToken(trigger)}; proof=control_ack");
        return true;
    }

    private static TimeSpan ResolveAccelerationControlBulkBypassWait()
        => AccelerationControlBulkBypassWaitOverrideForTests ?? AccelerationControlBulkBypassWait;

    private static TimeSpan ResolveAccelerationControlDirectSendWait()
        => AccelerationControlDirectSendWaitOverrideForTests ?? AccelerationControlDirectSendWait;

    private async Task<bool> SendAccelerationControlPriorityCopyAsync(
        string destination,
        Envelope envelope,
        byte[] bytes,
        string purpose,
        CancellationToken ct)
    {
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_control_priority_started; purpose={SanitizeLogToken(purpose)}; message_type={MapAccelerationControlMessageType(envelope.Type)}; lane=control_priority");
        try
        {
            return await TrySendAccelerationControlDirectCopyAsync(
                destination,
                bytes,
                purpose,
                envelope.Type,
                "control_priority",
                "tuna_acceleration_control_priority_sent",
                "tuna_acceleration_control_priority_failed",
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_acceleration_control_priority_failed; purpose={SanitizeLogToken(purpose)}; message_type={MapAccelerationControlMessageType(envelope.Type)}; error={ex.GetType().Name}");
            return false;
        }
    }

    private async Task<AccelerationControlSendResult> SendAccelerationControlBulkCopyAsync(
        string destination,
        Envelope envelope,
        byte[] bytes,
        string purpose,
        CancellationToken ct,
        Func<string?>? skipBulkQueueFallbackObservedProofReason = null,
        Func<string?>? continueToBulkQueueFallbackAfterDirectSuccessReason = null,
        Func<string?>? bulkQueueFallbackObservedProofFailureReason = null)
    {
        try
        {
            var endpointPreSendFailureReason =
                envelope.Type == MsgType.TransportAccelerationOffer &&
                IsTunaActivationOfferSendPurpose(purpose)
                    ? bulkQueueFallbackObservedProofFailureReason?.Invoke()
                    : null;
            if (!string.IsNullOrWhiteSpace(endpointPreSendFailureReason))
            {
                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_control_bulk_endpoint_observed_untrusted; purpose={SanitizeLogToken(purpose)}; message_type={MapAccelerationControlMessageType(envelope.Type)}; observed_lane=control_to_bulk_endpoint; reason={SanitizeLogToken(endpointPreSendFailureReason)}; stage=pre_send; peer_visible_proof_required=1");
                return AccelerationControlSendResult.Failed;
            }

            if (await TrySendAccelerationControlDirectCopyAsync(
                    destination,
                    bytes,
                    purpose,
                    envelope.Type,
                    "control_to_bulk_endpoint",
                    "tuna_acceleration_control_bulk_bypass_sent",
                    "tuna_acceleration_control_bulk_bypass_priority_failed",
                    ct).ConfigureAwait(false))
            {
                var continueReason = continueToBulkQueueFallbackAfterDirectSuccessReason?.Invoke();
                if (!string.IsNullOrWhiteSpace(continueReason))
                {
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_control_bulk_endpoint_observed_untrusted; purpose={SanitizeLogToken(purpose)}; message_type={MapAccelerationControlMessageType(envelope.Type)}; observed_lane=control_to_bulk_endpoint; reason={SanitizeLogToken(continueReason)}; fallback_lane=bulk_queue_fallback");
                }
                else
                {
                    return AccelerationControlSendResult.Success(AccelerationObservedLaneControlToBulkEndpoint);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_acceleration_control_bulk_bypass_priority_failed; purpose={SanitizeLogToken(purpose)}; message_type={MapAccelerationControlMessageType(envelope.Type)}; error={ex.GetType().Name}");
        }

        var skipReason = skipBulkQueueFallbackObservedProofReason?.Invoke();
        if (!string.IsNullOrWhiteSpace(skipReason))
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_acceleration_control_bulk_queue_fallback_skipped; purpose={SanitizeLogToken(purpose)}; message_type={MapAccelerationControlMessageType(envelope.Type)}; reason=runtime_unlock_active_filetransfer_requires_direct_observed_send; blocker_reason={SanitizeLogToken(skipReason)}");
            return AccelerationControlSendResult.Failed;
        }

        try
        {
            var preSendFailureReason = bulkQueueFallbackObservedProofFailureReason?.Invoke();
            if (!string.IsNullOrWhiteSpace(preSendFailureReason))
            {
                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_control_bulk_queue_fallback_observed_untrusted; purpose={SanitizeLogToken(purpose)}; message_type={MapAccelerationControlMessageType(envelope.Type)}; lane=bulk_queue_fallback; reason={SanitizeLogToken(preSendFailureReason)}; stage=pre_send");
                return AccelerationControlSendResult.Failed;
            }

            await SendBulkEnvelopeAsync(destination, envelope, bytes, ct, allowAcceleration: false).ConfigureAwait(false);
            var failureReason = bulkQueueFallbackObservedProofFailureReason?.Invoke();
            if (!string.IsNullOrWhiteSpace(failureReason))
            {
                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_control_bulk_queue_fallback_observed_untrusted; purpose={SanitizeLogToken(purpose)}; message_type={MapAccelerationControlMessageType(envelope.Type)}; lane=bulk_queue_fallback; reason={SanitizeLogToken(failureReason)}");
                return AccelerationControlSendResult.Failed;
            }

            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_control_bulk_bypass_sent; purpose={SanitizeLogToken(purpose)}; message_type={MapAccelerationControlMessageType(envelope.Type)}; lane=bulk_queue_fallback");
            return AccelerationControlSendResult.Success(AccelerationObservedLaneBulkQueueFallback);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_acceleration_control_bulk_bypass_failed; purpose={SanitizeLogToken(purpose)}; message_type={MapAccelerationControlMessageType(envelope.Type)}; error={ex.GetType().Name}");
            return AccelerationControlSendResult.Failed;
        }
    }

    private async Task<bool> TrySendAccelerationControlDirectCopyAsync(
        string destination,
        byte[] bytes,
        string purpose,
        MsgType messageType,
        string lane,
        string sentEvent,
        string failedEvent,
        CancellationToken ct)
    {
        Task sendTask;
        try
        {
            NknRuntimeDiagnostics.IncrementMessagesSent();
            sendTask = client.SendAsync(destination, bytes, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event={failedEvent}; purpose={SanitizeLogToken(purpose)}; message_type={MapAccelerationControlMessageType(messageType)}; lane={SanitizeLogToken(lane)}; error={ex.GetType().Name}");
            return false;
        }

        var waitTimeout = ResolveAccelerationControlDirectSendWait();
        var timeoutTask = Task.Delay(waitTimeout, ct);
        var completed = await Task.WhenAny(sendTask, timeoutTask).ConfigureAwait(false);
        if (!ReferenceEquals(completed, sendTask))
        {
            if (ct.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();
            }

            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event={failedEvent}; purpose={SanitizeLogToken(purpose)}; message_type={MapAccelerationControlMessageType(messageType)}; lane={SanitizeLogToken(lane)}; error=Timeout; wait_ms={(long)waitTimeout.TotalMilliseconds}");
            ObserveAccelerationControlDirectSendLateTask(sendTask, purpose, lane);
            return false;
        }

        try
        {
            await sendTask.ConfigureAwait(false);
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event={sentEvent}; purpose={SanitizeLogToken(purpose)}; message_type={MapAccelerationControlMessageType(messageType)}; lane={SanitizeLogToken(lane)}");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event={failedEvent}; purpose={SanitizeLogToken(purpose)}; message_type={MapAccelerationControlMessageType(messageType)}; lane={SanitizeLogToken(lane)}; error={ex.GetType().Name}");
            return false;
        }
    }

    private static void ObserveAccelerationControlDirectSendLateTask(Task task, string purpose, string lane)
    {
        _ = task.ContinueWith(
            completed =>
            {
                if (completed.IsFaulted)
                {
                    var ex = completed.Exception?.GetBaseException();
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_control_direct_send_late_failure; purpose={SanitizeLogToken(purpose)}; lane={SanitizeLogToken(lane)}; error={ex?.GetType().Name ?? "unknown"}");
                }
                else if (completed.IsCanceled)
                {
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_control_direct_send_late_canceled; purpose={SanitizeLogToken(purpose)}; lane={SanitizeLogToken(lane)}");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void ObserveAccelerationControlSendTask(Task<bool> task, string purpose, string lane)
    {
        _ = task.ContinueWith(
            completed =>
            {
                if (completed.IsFaulted)
                {
                    var ex = completed.Exception?.GetBaseException();
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_control_send_late_failure; purpose={SanitizeLogToken(purpose)}; lane={SanitizeLogToken(lane)}; error={ex?.GetType().Name ?? "unknown"}");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void ObserveAccelerationControlSendTask(Task<AccelerationControlSendResult> task, string purpose, string lane)
    {
        _ = task.ContinueWith(
            completed =>
            {
                if (completed.IsFaulted)
                {
                    var ex = completed.Exception?.GetBaseException();
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_control_send_late_failure; purpose={SanitizeLogToken(purpose)}; lane={SanitizeLogToken(lane)}; error={ex?.GetType().Name ?? "unknown"}");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static string MapAccelerationControlMessageType(MsgType messageType)
        => messageType switch
        {
            MsgType.TransportAccelerationOffer => "transport_acceleration_offer",
            MsgType.TransportAccelerationAnswer => "transport_acceleration_answer",
            MsgType.TransportAccelerationAnswerAck => "transport_acceleration_answer_ack",
            MsgType.TransportAccelerationDown => "transport_acceleration_down",
            MsgType.TransportAccelerationPayerIntent => "transport_acceleration_payer_intent",
            _ => SanitizeLogToken(messageType.ToString()),
        };

    private void SendAccelerationLifecycleAckIfValid(string source, Envelope env, string messageType)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        SendAckFireAndForget(source, env.Code, env.MessageId);
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_lifecycle_ack_sent; message_type={SanitizeLogToken(messageType)}; msg_id={SanitizeLogToken(env.MessageId)}");
    }

    private void HandleTransportAccelerationAnswer(string source, Envelope env)
    {
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_answer_received_raw; msg_id={SanitizeLogToken(env.MessageId)}");
        if (!TryDecryptControlPayload(source, env, MsgType.TransportAccelerationAnswer, out var securePayload))
        {
            return;
        }

        if (!TryDeserializeAccelerationPayload<TransportAccelerationAnswerPayload>(securePayload.Plaintext, out var answer) ||
            answer is null)
        {
            RejectAccelerationEnvelope("transport_acceleration_answer", "payload_invalid", env.MessageId);
            return;
        }

        if (!TryValidateControlSecureMetadata("transport_acceleration_answer", securePayload.Metadata, answer.Nonce, env.MessageId))
        {
            return;
        }

        SendAccelerationLifecycleAckIfValid(source, env, "transport_acceleration_answer");

        string? expectedNonce;
        string? expectedTrigger;
        long expectedPayerDecisionId;
        long expectedOfferGeneration;
        bool matchedRetiredOffer;
        bool matchedRetiredRuntimeUnlockGeneration;
        var answerSessionId = string.IsNullOrWhiteSpace(answer.SessionId) ? string.Empty : answer.SessionId.Trim();
        var answerNonce = string.IsNullOrWhiteSpace(answer.Nonce) ? string.Empty : answer.Nonce.Trim();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (accelerationGate)
        {
            matchedRetiredOffer = false;
            matchedRetiredRuntimeUnlockGeneration = false;
            if (!string.IsNullOrWhiteSpace(outboundAccelerationOfferNonce) &&
                string.Equals(outboundAccelerationOfferNonce, answerNonce, StringComparison.Ordinal))
            {
                expectedNonce = outboundAccelerationOfferNonce;
                expectedTrigger = outboundAccelerationOfferTrigger;
                expectedPayerDecisionId = outboundAccelerationOfferPayerDecisionId;
                expectedOfferGeneration = outboundAccelerationOfferGeneration;
                var state = runtimeUnlockOfferProofState;
                if (state is not null &&
                    !state.Retired &&
                    state.PayerDecisionId == expectedPayerDecisionId &&
                    string.Equals(state.Nonce, answerNonce, StringComparison.Ordinal))
                {
                    state.PeerReceived = true;
                }
            }
            else if (!string.IsNullOrWhiteSpace(retiredAccelerationOfferNonce) &&
                     string.Equals(retiredAccelerationOfferNonce, answerNonce, StringComparison.Ordinal) &&
                     string.Equals(retiredAccelerationOfferSessionId, answerSessionId, StringComparison.Ordinal) &&
                     nowMs < retiredAccelerationOfferExpiresUtcMs)
            {
                expectedNonce = retiredAccelerationOfferNonce;
                expectedTrigger = retiredAccelerationOfferTrigger;
                expectedPayerDecisionId = retiredAccelerationOfferPayerDecisionId;
                expectedOfferGeneration = runtimeUnlockOfferProofState?.Generation ?? 0;
                matchedRetiredOffer = true;
                var state = runtimeUnlockOfferProofState;
                matchedRetiredRuntimeUnlockGeneration =
                    state is not null &&
                    ShouldRejectRetiredRuntimeUnlockAnswer(state) &&
                    state.PayerDecisionId == expectedPayerDecisionId &&
                    string.Equals(state.Nonce, answerNonce, StringComparison.Ordinal) &&
                    string.Equals(state.SessionId, answerSessionId, StringComparison.Ordinal);
            }
            else
            {
                expectedNonce = null;
                expectedTrigger = null;
                expectedPayerDecisionId = 0;
                expectedOfferGeneration = 0;
            }
        }

        if (string.IsNullOrWhiteSpace(expectedNonce) ||
            !string.Equals(expectedNonce, answerNonce, StringComparison.Ordinal))
        {
            RejectAccelerationEnvelope("transport_acceleration_answer", "nonce_mismatch", env.MessageId);
            ResumeFileTransferDataSessionsAfterTunaActivationFailure(
                "nonce_mismatch",
                answer.SessionId,
                "transport_acceleration_answer");
            return;
        }

        if (matchedRetiredRuntimeUnlockGeneration)
        {
            lock (accelerationGate)
            {
                ClearRetiredOutboundAccelerationOfferLocked();
            }

            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_acceleration_stale_offer_answer_ignored; reason=retired_generation; session_id={SanitizeLogToken(answer.SessionId)}; payer_decision_id={answer.PayerDecisionId}");
            RejectAccelerationEnvelope("transport_acceleration_answer", "stale_offer_generation", env.MessageId);
            return;
        }

        if (expectedPayerDecisionId > 0 &&
            answer.PayerDecisionId != expectedPayerDecisionId)
        {
            RejectAccelerationEnvelope("transport_acceleration_answer", "payer_decision_mismatch", env.MessageId);
            ResumeFileTransferDataSessionsAfterTunaActivationFailure(
                "payer_decision_mismatch",
                answer.SessionId,
                "transport_acceleration_answer");
            return;
        }

        if (IsRuntimeUnlockActivationReason(expectedTrigger) &&
            expectedOfferGeneration > 0)
        {
            if (!MarkRuntimeUnlockCutThroughPeerReceived(
                answer.SessionId,
                answer.PayerDecisionId,
                answerNonce,
                "transport_acceleration_answer"))
            {
                MarkRuntimeUnlockPeerReceivedIfCurrent(
                    answer.SessionId,
                    answer.PayerDecisionId,
                    answerNonce,
                    expectedOfferGeneration,
                    "transport_acceleration_answer");
            }
        }

        var validation = ValidateAccelerationAnswer(source, answer, requireAcceptedLanes: answer.Accepted);
        if (validation.IsHardReject)
        {
            RejectAccelerationEnvelope("transport_acceleration_answer", validation.Reason ?? "invalid", env.MessageId);
            ResumeFileTransferDataSessionsAfterTunaActivationFailure(
                validation.Reason ?? "invalid",
                answer.SessionId,
                "transport_acceleration_answer");
            return;
        }

        if (!answer.Accepted || !validation.IsValid)
        {
            var effectiveRejectReason = !answer.Accepted
                ? answer.RejectReason ?? validation.Reason ?? "rejected"
                : validation.Reason ?? answer.RejectReason ?? "rejected";
            lock (accelerationGate)
            {
                if (matchedRetiredOffer)
                {
                    ClearRetiredOutboundAccelerationOfferLocked();
                }
                else
                {
                    ClearOutboundAccelerationOfferLocked();
                }
            }

            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_answer_rejected; reason={SanitizeLogToken(effectiveRejectReason)}; offer_trigger={SanitizeLogToken(expectedTrigger)}; payer_decision_id={expectedPayerDecisionId}");
            NotifyTransportAccelerationStateChanged($"answer_rejected_{effectiveRejectReason}");
            ResumeFileTransferDataSessionsAfterTunaActivationFailure(
                effectiveRejectReason,
                answer.SessionId,
                "transport_acceleration_answer_rejected");
            if (IsRuntimeUnlockActivationReason(expectedTrigger) &&
                expectedOfferGeneration > 0)
            {
                var normalizedRejectReason = SanitizeLogToken(effectiveRejectReason);
                if (normalizedRejectReason is "sidecar_unavailable" or "listener_sidecar_unavailable")
                {
                    var leaseReason = "runtime_unlock_answer_rejected_tuna_path_lease_unavailable";
                    FailRuntimeUnlockTunaPathLeaseIfCurrent(
                        answer.SessionId,
                        expectedOfferGeneration,
                        leaseReason,
                        failTransaction: true);
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=runtime_unlock_answer_rejected_tuna_path_lease_unavailable; session_id={SanitizeLogToken(answer.SessionId)}; payer_decision_id={expectedPayerDecisionId}; offer_generation={expectedOfferGeneration}; reject_reason={SanitizeLogToken(effectiveRejectReason)}");
                }

                FailRuntimeUnlockCutThroughIfCurrent(
                    answer.SessionId,
                    answer.PayerDecisionId,
                    expectedOfferGeneration,
                    $"runtime_unlock_answer_rejected_{SanitizeLogToken(effectiveRejectReason)}");
            }

            if (string.Equals(SanitizeLogToken(effectiveRejectReason), "helpee_payer_preferred", StringComparison.Ordinal))
            {
                AdvancePayerDecisionEpoch("yield_to_helpee_payer");
                NotifyTransportAccelerationStateChanged("suppressed_by_peer_payer");
                ScheduleAccelerationLaneStop("payer_yield_to_helpee");
                return;
            }

            var retryPeerStopAfterUnlock = ShouldRetryPeerUserStoppedAfterRuntimeUnlock(effectiveRejectReason, expectedTrigger);
            var retryRuntimeUnlockAfterRejectedAnswer =
                ShouldRetryRuntimeUnlockAfterRejectedAnswer(effectiveRejectReason, expectedTrigger);
            if (ShouldRetryAccelerationNegotiation(effectiveRejectReason) ||
                retryPeerStopAfterUnlock ||
                retryRuntimeUnlockAfterRejectedAnswer)
            {
                var retryReason = retryRuntimeUnlockAfterRejectedAnswer
                    ? $"runtime_unlock_{SanitizeLogToken(effectiveRejectReason)}"
                    : retryPeerStopAfterUnlock
                        ? "peer_user_stopped_tuna"
                        : effectiveRejectReason!;
                if (retryRuntimeUnlockAfterRejectedAnswer &&
                    IsRuntimeUnlockListenerRearmRequiredRetryReason(retryReason))
                {
                    var listenerReadyReuse = ShouldUseListenerReadyFastRetry(retryReason);
                    if (listenerReadyReuse)
                    {
                        LocalOperationalLog.Warn(
                            "NKN.Tuna",
                            $"event=session_recovery_contract_listener_rearm_skipped; session_id={SanitizeLogToken(answer.SessionId)}; reason={SanitizeLogToken(retryReason)}; trigger=transport_acceleration_answer_rejected; payer_decision_id={expectedPayerDecisionId}; listener_ready_reuse=1");
                    }
                    else
                    {
                        LocalOperationalLog.Warn(
                            "NKN.Tuna",
                            $"event=session_recovery_contract_listener_rearm_required; session_id={SanitizeLogToken(answer.SessionId)}; reason={SanitizeLogToken(retryReason)}; trigger=transport_acceleration_answer_rejected; payer_decision_id={expectedPayerDecisionId}; listener_ready_reuse=0");
                        ScheduleAccelerationLaneStop(retryReason);
                    }
                }

                ScheduleAccelerationNegotiationRetry(retryReason);
            }

            return;
        }

        if (IsRuntimeUnlockActivationReason(expectedTrigger))
        {
            if (expectedOfferGeneration > 0)
            {
                if (IsRuntimeUnlockTunaPathLeaseUnavailableForAnswer(
                        answer.SessionId,
                        expectedOfferGeneration,
                        out var leaseUnavailableReason))
                {
                    var leaseReason = "runtime_unlock_answer_rejected_tuna_path_lease_unavailable";
                    FailRuntimeUnlockTunaPathLeaseIfCurrent(
                        answer.SessionId,
                        expectedOfferGeneration,
                        leaseReason,
                        failTransaction: true);
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=runtime_unlock_answer_rejected_tuna_path_lease_unavailable; session_id={SanitizeLogToken(answer.SessionId)}; payer_decision_id={expectedPayerDecisionId}; offer_generation={expectedOfferGeneration}; reject_reason={SanitizeLogToken(leaseUnavailableReason)}");
                    NotifyTransportAccelerationStateChanged("answer_rejected_tuna_path_lease_unavailable");
                    ResumeFileTransferDataSessionsAfterTunaActivationFailure(
                        "tuna_path_lease_unavailable",
                        answer.SessionId,
                        "transport_acceleration_answer_lease_unavailable");
                    ScheduleAccelerationLaneStop("runtime_unlock_sidecar_unavailable");
                    ScheduleAccelerationNegotiationRetry("runtime_unlock_sidecar_unavailable");
                    return;
                }

                if (!MarkRuntimeUnlockCutThroughAnswerReceived(
                    answer.SessionId,
                    answer.PayerDecisionId,
                    expectedOfferGeneration,
                    "transport_acceleration_answer"))
                {
                    MarkRuntimeUnlockAnswerReceivedIfCurrent(
                        answer.SessionId,
                        answer.PayerDecisionId,
                        answerNonce,
                        expectedOfferGeneration,
                        "transport_acceleration_answer");
                }
            }

            PauseFileTransferDataSessionsForTunaActivationNegotiation(
                "activation_negotiation_pending",
                answer.SessionId,
                "answer_accepted");
        }

        lock (accelerationGate)
        {
            if (matchedRetiredOffer)
            {
                ClearRetiredOutboundAccelerationOfferLocked();
                ClearOutboundAccelerationOfferLocked();
            }
            else
            {
                ClearOutboundAccelerationOfferLocked();
            }

            ClearPendingAccelerationAnswerAckLocked();
            accelerationSessionId = answer.SessionId.Trim();
            accelerationNegotiatedLanes = validation.AcceptedLanes;
        }

        if (matchedRetiredOffer)
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_retired_offer_answer_accepted; session_id={SanitizeLogToken(answer.SessionId)}; payer_decision_id={answer.PayerDecisionId}");
        }

        _ = Task.Run(
            () => SendAccelerationAnswerAckAsync(answer, validation.AcceptedLanes, CancellationToken.None),
            CancellationToken.None);
        Interlocked.Exchange(ref accelerationNegotiationRetryAttempts, 0);
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_negotiated; session_id={answer.SessionId}; lanes={string.Join(",", answer.SupportedLanes)}; payer_decision_id={answer.PayerDecisionId}");
        CompleteRuntimeUnlockRecoveryContractIfActive(answer.SessionId, "transport_acceleration_answer");
        RequestFileTransferTunaActivationHandoff(answer.SessionId, validation.AcceptedLanes, "tuna_activation_negotiated");
        NotifyTransportAccelerationStateChanged(GetActiveAccelerationStatusReason());
    }

    private static bool ShouldRejectRetiredRuntimeUnlockAnswer(RuntimeUnlockOfferProofState state)
    {
        if (!state.Retired)
        {
            return false;
        }

        if (!state.ObservedSend && !state.AnswerTimeoutScheduled)
        {
            return true;
        }

        return state.RetiredReason is "offer_answer_timeout" ||
               !IsRuntimeUnlockPendingAnswerRetiredReason(state.RetiredReason);
    }

    private void HandleTransportAccelerationAnswerAck(string source, Envelope env)
    {
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_answer_ack_received_raw; msg_id={SanitizeLogToken(env.MessageId)}");
        if (!TryDecryptControlPayload(source, env, MsgType.TransportAccelerationAnswerAck, out var securePayload))
        {
            return;
        }

        if (!TryDeserializeAccelerationPayload<TransportAccelerationAnswerAckPayload>(securePayload.Plaintext, out var ack) ||
            ack is null)
        {
            RejectAccelerationEnvelope("transport_acceleration_answer_ack", "payload_invalid", env.MessageId);
            return;
        }

        if (!TryValidateControlSecureMetadata("transport_acceleration_answer_ack", securePayload.Metadata, ack.Nonce, env.MessageId))
        {
            return;
        }

        SendAccelerationLifecycleAckIfValid(source, env, "transport_acceleration_answer_ack");

        var validation = ValidateAccelerationAnswerAck(source, ack);
        if (validation.IsHardReject || !validation.IsValid || !ack.Accepted)
        {
            RejectAccelerationEnvelope(
                "transport_acceleration_answer_ack",
                validation.Reason ?? (ack.Accepted ? "invalid" : "not_accepted"),
                env.MessageId);
            ResumeFileTransferDataSessionsAfterTunaActivationFailure(
                validation.Reason ?? (ack.Accepted ? "invalid" : "not_accepted"),
                ack.SessionId,
                "transport_acceleration_answer_ack");
            return;
        }

        var pendingLanes = NknAccelerationLaneKind.None;
        var pendingPayerDecisionId = 0L;
        bool alreadyNegotiated;
        string? activationFailureReason = null;
        lock (accelerationGate)
        {
            alreadyNegotiated =
                accelerationLane?.IsAvailable == true &&
                accelerationNegotiatedLanes != NknAccelerationLaneKind.None &&
                string.Equals(accelerationSessionId, ack.SessionId.Trim(), StringComparison.Ordinal);
            if (string.IsNullOrWhiteSpace(pendingAccelerationAnswerAckNonce))
            {
                if (alreadyNegotiated)
                {
                    LocalOperationalLog.Info(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_answer_ack_duplicate_ignored; session_id={SanitizeLogToken(ack.SessionId)}; payer_decision_id={ack.PayerDecisionId}");
                    return;
                }
            }

            if (!string.Equals(pendingAccelerationAnswerAckNonce, ack.Nonce.Trim(), StringComparison.Ordinal))
            {
                RejectAccelerationEnvelope("transport_acceleration_answer_ack", "nonce_mismatch", env.MessageId);
                activationFailureReason = "nonce_mismatch";
            }
            else if (!string.Equals(pendingAccelerationAnswerAckSessionId, ack.SessionId.Trim(), StringComparison.Ordinal))
            {
                RejectAccelerationEnvelope("transport_acceleration_answer_ack", "session_id_mismatch", env.MessageId);
                activationFailureReason = "session_id_mismatch";
            }
            else
            {
                pendingLanes = pendingAccelerationAnswerAckLanes;
                pendingPayerDecisionId = pendingAccelerationAnswerAckPayerDecisionId;
                if (pendingPayerDecisionId > 0 && ack.PayerDecisionId != pendingPayerDecisionId)
                {
                    RejectAccelerationEnvelope("transport_acceleration_answer_ack", "payer_decision_mismatch", env.MessageId);
                    activationFailureReason = "payer_decision_mismatch";
                }
                else
                {
                    var acceptedLanes = validation.AcceptedLanes & pendingLanes;
                    if (acceptedLanes == NknAccelerationLaneKind.None)
                    {
                        RejectAccelerationEnvelope("transport_acceleration_answer_ack", "unsupported_lane", env.MessageId);
                        activationFailureReason = "unsupported_lane";
                    }
                    else
                    {
                        accelerationSessionId = ack.SessionId.Trim();
                        accelerationNegotiatedLanes = acceptedLanes;
                        ClearPendingAccelerationAnswerAckLocked();
                    }
                }
            }
        }

        if (activationFailureReason is not null)
        {
            ResumeFileTransferDataSessionsAfterTunaActivationFailure(
                activationFailureReason,
                ack.SessionId,
                "transport_acceleration_answer_ack");
            return;
        }

        Interlocked.Exchange(ref accelerationNegotiationRetryAttempts, 0);
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_answer_ack_received; session_id={SanitizeLogToken(ack.SessionId)}; lanes={FormatAccelerationLanesForLog(validation.AcceptedLanes & pendingLanes)}; payer_decision_id={ack.PayerDecisionId}");
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_negotiated; session_id={ack.SessionId}; lanes={string.Join(",", ack.SupportedLanes)}; payer_decision_id={ack.PayerDecisionId}; handshake=answer_ack");
        CompleteRuntimeUnlockRecoveryContractIfActive(ack.SessionId, "transport_acceleration_answer_ack");
        RequestFileTransferTunaActivationHandoff(ack.SessionId, validation.AcceptedLanes & pendingLanes, "tuna_activation_answer_ack");
        NotifyTransportAccelerationStateChanged(GetActiveAccelerationStatusReason());
    }

    private void HandleTransportAccelerationDown(string source, Envelope env)
    {
        if (!TryDecryptControlPayload(source, env, MsgType.TransportAccelerationDown, out var securePayload))
        {
            return;
        }

        if (!TryDeserializeAccelerationPayload<TransportAccelerationDownPayload>(securePayload.Plaintext, out var down) ||
            down is null)
        {
            RejectAccelerationEnvelope("transport_acceleration_down", "payload_invalid", env.MessageId);
            return;
        }

        if (!TryValidateControlSecureMetadata("transport_acceleration_down", securePayload.Metadata, down.Nonce, env.MessageId))
        {
            return;
        }

        var rejectReason = ValidateAccelerationDown(source, down);
        if (rejectReason is not null)
        {
            RejectAccelerationEnvelope("transport_acceleration_down", rejectReason, env.MessageId);
            return;
        }

        var downReason = $"remote_{down.Reason}";
        var downLanes = NknAccelerationLaneCodec.FromNames(down.SupportedLanes);
        var isUserRequestedDown = IsUserRequestedAccelerationStopReason(down.Reason);
        if (isUserRequestedDown)
        {
            MarkAccelerationPeerUserStoppedForCurrentSession(down.SessionId);
        }

        if (IsAccelerationNegotiatedAndHealthy())
        {
            ResetAccelerationNegotiation(downReason);
            ScheduleAccelerationLaneStop(downReason);
        }
        else if (isUserRequestedDown)
        {
            ResetAccelerationNegotiation(downReason);
            ScheduleAccelerationLaneStop(downReason);
        }
        else if (StartTunaFallbackProofIfNeeded(downReason, down.SessionId, downLanes))
        {
            RebindFileTransferDataSessionsForTunaFallback(downReason, down.SessionId, downLanes);
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_remote_down; reason={SanitizeLogToken(down.Reason)}; lanes={string.Join(",", down.SupportedLanes)}");
    }

    private bool ScheduleAccelerationNegotiationRetry(string reason)
    {
        var normalizedReason = SanitizeLogToken(reason);
        var isRuntimeUnlockActivationRetry = IsRuntimeUnlockActivationRetryReason(normalizedReason);
        if (disposed)
        {
            LogAccelerationRetryNotScheduled(normalizedReason, "disposed");
            return false;
        }

        if (accelerationLane is not INknTunaAccelerationSession)
        {
            LogAccelerationRetryNotScheduled(normalizedReason, "missing_acceleration_lane");
            return false;
        }

        if (IsAccelerationNegotiatedAndHealthy() &&
            !ShouldAllowRuntimeUnlockRetryDespiteHealthyTransport(normalizedReason))
        {
            LogAccelerationRetryNotScheduled(normalizedReason, "already_healthy");
            return false;
        }

        if (!IsSessionAccelerationEligible(out _))
        {
            LogAccelerationRetryNotScheduled(normalizedReason, "session_not_eligible");
            return false;
        }

        if (isRuntimeUnlockActivationRetry &&
            ShouldSuppressRuntimeUnlockRetryAfterTerminalTransactionFailure(normalizedReason))
        {
            LogAccelerationRetryNotScheduled(normalizedReason, "runtime_unlock_transaction_failed");
            return false;
        }

        if (isRuntimeUnlockActivationRetry &&
            !TryConsumeRuntimeUnlockBrokenStateRetryBudget(normalizedReason))
        {
            LogAccelerationRetryNotScheduled(normalizedReason, "runtime_unlock_broken_state_finalized");
            return false;
        }

        if (TryGetUnresolvedFileTransferV6TransportEpochForCurrentSession(out var pendingEpoch) &&
            !ShouldAllowAccelerationRetryDespiteUnresolvedV6Epoch(pendingEpoch, normalizedReason, "preflight"))
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_acceleration_retry_blocked_v6_epoch_unresolved; reason={normalizedReason}; session_id={SanitizeLogToken(pendingEpoch.SessionId)}; transfer_id={SanitizeLogToken(pendingEpoch.TransferId)}; direction={pendingEpoch.Direction.ToString().ToLowerInvariant()}; transport_epoch={pendingEpoch.TransportEpoch}; state={FormatFileTransferV6TransportEpochStateForLog(pendingEpoch.State)}; handoff_kind={FormatFileTransferTransportHandoffKindForLog(pendingEpoch.HandoffKind)}; target_transport={FormatFileTransferTransportKindForLog(pendingEpoch.TargetTransport)}");
            return false;
        }

        if (TryGetFileTransferFallbackControlProofPendingSnapshot(out var pendingSessionId, out var pendingReason, out var pendingLanes) &&
            !ShouldAllowAccelerationRetryDespiteFallbackControlProofPending(
                pendingSessionId,
                pendingReason,
                pendingLanes,
                normalizedReason,
                "preflight"))
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_acceleration_retry_blocked_fallback_control_unproven; reason={normalizedReason}; fallback_reason={SanitizeLogToken(pendingReason)}; session_id={SanitizeLogToken(pendingSessionId ?? "none")}; lanes={FormatAccelerationLanesForLog(pendingLanes)}");
            return false;
        }

        var maxAttempts = isRuntimeUnlockActivationRetry
            ? RuntimeUnlockAccelerationNegotiationMaxRetryAttempts
            : AccelerationNegotiationMaxRetryAttempts;
        var attempt = Interlocked.Increment(ref accelerationNegotiationRetryAttempts);
        if (attempt > maxAttempts)
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_acceleration_retry_exhausted; reason={normalizedReason}; attempts={attempt - 1}");
            if (isRuntimeUnlockActivationRetry)
            {
                FailRuntimeUnlockRecoveryContractIfActive(
                    currentSessionSecurityState.SessionId?.Value,
                    $"retry_exhausted_{normalizedReason}");
            }

            if (TryGetActiveFileTransferTunaActivationPauseForCurrentSession(out var pausedSessionId))
            {
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=filetransfer_tuna_activation_retry_exhausted_resuming_regular_nkn; reason={normalizedReason}; session_id={SanitizeLogToken(pausedSessionId ?? "none")}; attempts={attempt - 1}");
                ResumeFileTransferDataSessionsAfterTunaActivationNegotiation(
                    $"retry_exhausted_{normalizedReason}",
                    pausedSessionId,
                    "retry_exhausted");
                return false;
            }

            ResumeFileTransferDataSessionsAfterTunaActivationNegotiation(
                $"retry_exhausted_{normalizedReason}",
                currentSessionSecurityState.SessionId?.Value,
                "retry_exhausted");
            return false;
        }

        var useListenerReadyFastRetry = ShouldUseListenerReadyFastRetry(normalizedReason);
        var delay = useListenerReadyFastRetry
            ? AccelerationListenerReadyRetryDelay
            : TimeSpan.FromMilliseconds(
                AccelerationNegotiationRetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
        var genericDelay = delay;
        if (TryGetRuntimeUnlockRecoveryContractRetryDelay(
                normalizedReason,
                delay,
                out var contractDelay,
                out var contractRetryReason))
        {
            delay = contractDelay;
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=session_recovery_contract_retry_backoff_capped; reason={normalizedReason}; attempt={attempt}; generic_delay_ms={(int)genericDelay.TotalMilliseconds}; capped_delay_ms={(int)delay.TotalMilliseconds}; cap_reason={SanitizeLogToken(contractRetryReason)}");
        }
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_retry_scheduled; reason={normalizedReason}; attempt={attempt}; max_attempts={maxAttempts}; delay_ms={(int)delay.TotalMilliseconds}; listener_ready_reuse={(useListenerReadyFastRetry ? 1 : 0)}");
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(delay, CancellationToken.None).ConfigureAwait(false);
                    if (disposed)
                    {
                        LogAccelerationRetryNotScheduled(normalizedReason, "delayed_disposed");
                        return;
                    }

                    if (IsAccelerationNegotiatedAndHealthy() &&
                        !ShouldAllowRuntimeUnlockRetryDespiteHealthyTransport(normalizedReason))
                    {
                        LogAccelerationRetryNotScheduled(normalizedReason, "delayed_already_healthy");
                        return;
                    }

                    if (HasPendingOutboundAccelerationOffer())
                    {
                        LogAccelerationRetryNotScheduled(normalizedReason, "delayed_pending_offer");
                        return;
                    }

                    if (HasPendingAccelerationAnswerAck())
                    {
                        LogAccelerationRetryNotScheduled(normalizedReason, "delayed_pending_answer_ack");
                        return;
                    }

                    if (!IsSessionAccelerationEligible(out _))
                    {
                        LogAccelerationRetryNotScheduled(normalizedReason, "delayed_session_not_eligible");
                        return;
                    }

                    if (TryGetUnresolvedFileTransferV6TransportEpochForCurrentSession(out var delayedPendingEpoch) &&
                        !ShouldAllowAccelerationRetryDespiteUnresolvedV6Epoch(delayedPendingEpoch, normalizedReason, "delayed"))
                    {
                        LocalOperationalLog.Warn(
                            "NKN.Tuna",
                            $"event=tuna_acceleration_retry_skipped_v6_epoch_unresolved; reason={normalizedReason}; session_id={SanitizeLogToken(delayedPendingEpoch.SessionId)}; transfer_id={SanitizeLogToken(delayedPendingEpoch.TransferId)}; direction={delayedPendingEpoch.Direction.ToString().ToLowerInvariant()}; transport_epoch={delayedPendingEpoch.TransportEpoch}; state={FormatFileTransferV6TransportEpochStateForLog(delayedPendingEpoch.State)}; handoff_kind={FormatFileTransferTransportHandoffKindForLog(delayedPendingEpoch.HandoffKind)}; target_transport={FormatFileTransferTransportKindForLog(delayedPendingEpoch.TargetTransport)}");
                        return;
                    }

                    if (TryGetFileTransferFallbackControlProofPendingSnapshot(out var delayedPendingSessionId, out var delayedPendingReason, out var delayedPendingLanes) &&
                        !ShouldAllowAccelerationRetryDespiteFallbackControlProofPending(
                            delayedPendingSessionId,
                            delayedPendingReason,
                            delayedPendingLanes,
                            normalizedReason,
                            "delayed"))
                    {
                        LocalOperationalLog.Warn(
                            "NKN.Tuna",
                            $"event=tuna_acceleration_retry_skipped_fallback_control_unproven; reason={normalizedReason}; fallback_reason={SanitizeLogToken(delayedPendingReason)}; session_id={SanitizeLogToken(delayedPendingSessionId ?? "none")}; lanes={FormatAccelerationLanesForLog(delayedPendingLanes)}");
                        return;
                    }

                    LocalOperationalLog.Info(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_retry_fired; reason={normalizedReason}; listener_ready_reuse={(useListenerReadyFastRetry ? 1 : 0)}");
                    ScheduleAccelerationNegotiationIfEligible(
                        isRuntimeUnlockActivationRetry ||
                        string.Equals(normalizedReason, "peer_user_stopped_tuna", StringComparison.Ordinal)
                            ? "runtime_unlock"
                            : $"retry_{normalizedReason}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_retry_failed; reason={normalizedReason}; error={ex.GetType().Name}");
                }
            },
            CancellationToken.None);
        return true;
    }

    private bool TryGetRuntimeUnlockRecoveryContractRetryDelay(
        string reason,
        TimeSpan currentDelay,
        out TimeSpan delay,
        out string capReason)
    {
        delay = currentDelay;
        capReason = string.Empty;
        if (!IsRuntimeUnlockActivationRetryReason(reason))
        {
            return false;
        }

        RuntimeUnlockRecoveryRetryState? stateSnapshot;
        lock (accelerationGate)
        {
            var state = runtimeUnlockRecoveryRetryState;
            if (state is null ||
                state.ContractState is SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed ||
                !string.Equals(state.RetryReason, reason, StringComparison.Ordinal) ||
                !IsSessionRecoveryContractRetryRequired(state))
            {
                return false;
            }

            stateSnapshot = state;
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var maxDelay = RuntimeUnlockRecoveryContractRetryMaxDelay;
        if (stateSnapshot.RetryDeadlineUtcMs > nowMs)
        {
            var retryDeadlineDelay = TimeSpan.FromMilliseconds(Math.Max(
                0,
                stateSnapshot.RetryDeadlineUtcMs -
                nowMs -
                (long)RuntimeUnlockRecoveryContractRetryDeadlineSlack.TotalMilliseconds));
            maxDelay = MinTimeSpan(maxDelay, retryDeadlineDelay);
        }

        if (stateSnapshot.LivenessDeferralDeadlineUtcMs > nowMs)
        {
            var livenessDeadlineDelay = TimeSpan.FromMilliseconds(Math.Max(
                0,
                stateSnapshot.LivenessDeferralDeadlineUtcMs -
                nowMs -
                (long)RuntimeUnlockRecoveryContractRetryDeadlineSlack.TotalMilliseconds));
            maxDelay = MinTimeSpan(maxDelay, livenessDeadlineDelay);
        }

        if (maxDelay < TimeSpan.Zero)
        {
            maxDelay = TimeSpan.Zero;
        }

        if (currentDelay <= maxDelay)
        {
            return false;
        }

        delay = maxDelay;
        capReason = stateSnapshot.RequiresLocalListenerRetry
            ? "runtime_unlock_listener_rearm_contract"
            : "runtime_unlock_recovery_contract";
        return true;
    }

    private static TimeSpan MinTimeSpan(TimeSpan left, TimeSpan right)
        => left <= right ? left : right;

    private void LogAccelerationRetryNotScheduled(string reason, string skipReason)
    {
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_retry_not_scheduled; reason={SanitizeLogToken(reason)}; skip_reason={SanitizeLogToken(skipReason)}");
    }

    private bool ShouldAllowRuntimeUnlockRetryDespiteHealthyTransport(string reason)
    {
        if (!IsRuntimeUnlockActivationRetryReason(reason))
        {
            return false;
        }

        var sessionId = currentSessionSecurityState.SessionId?.Value;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        if (HasActiveRegularV4FileTransferRouteHint(sessionId) ||
            HasActivePostTunaFallbackFileTransferRouteHint(sessionId))
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_retry_allowed_despite_healthy_transport; reason={SanitizeLogToken(reason)}; session_id={SanitizeLogToken(sessionId)}; active_filetransfer_route=1");
            return true;
        }

        if (HasActiveFileTransferDataSessionForSession(sessionId))
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_retry_allowed_despite_healthy_transport; reason={SanitizeLogToken(reason)}; session_id={SanitizeLogToken(sessionId)}; active_filetransfer_session=1");
            return true;
        }

        return TryGetUnresolvedFileTransferV6TransportEpochForCurrentSession(out var pendingEpoch) &&
               IsPostTunaFallbackRegularNknEpoch(pendingEpoch) &&
               ShouldAllowRuntimeUnlockRetryForActivePostTunaFallbackRepair(pendingEpoch);
    }

    private bool HasActiveFileTransferDataSessionForSession(string sessionId)
    {
        lock (gate)
        {
            foreach (var session in fileTransferDataSessions.Values)
            {
                if (!session.IsDisposed &&
                    string.Equals(session.SessionId, sessionId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsRegularNknRecoveryEpoch(FileTransferV6TransportEpochSnapshot snapshot)
        => snapshot.TargetTransport == FileTransferTransportKind.RegularNkn &&
           snapshot.HandoffKind == FileTransferTransportHandoffKind.RegularNknRecovery;

    private static bool IsPostTunaFallbackRegularNknEpoch(FileTransferV6TransportEpochSnapshot snapshot)
        => snapshot.TargetTransport == FileTransferTransportKind.RegularNkn &&
           snapshot.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback;

    private bool ShouldAllowAccelerationRetryDespiteUnresolvedV6Epoch(
        FileTransferV6TransportEpochSnapshot pendingEpoch,
        string reason,
        string stage)
    {
        if (IsPostTunaFallbackRegularNknEpoch(pendingEpoch) &&
            IsRuntimeUnlockActivationRetryReason(reason))
        {
            if (TryGetCurrentPostTunaFallbackObservedSendProbeProof(
                    pendingEpoch.SessionId,
                    out var proof,
                    out var proofDirection,
                    out var proofAgeMs))
            {
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_retry_allowed_post_tuna_fallback_current_authority; reason={SanitizeLogToken(reason)}; stage={SanitizeLogToken(stage)}; session_id={SanitizeLogToken(pendingEpoch.SessionId)}; transfer_id={SanitizeLogToken(pendingEpoch.TransferId)}; direction={pendingEpoch.Direction.ToString().ToLowerInvariant()}; transport_epoch={pendingEpoch.TransportEpoch}; state={FormatFileTransferV6TransportEpochStateForLog(pendingEpoch.State)}; handoff_kind={FormatFileTransferTransportHandoffKindForLog(pendingEpoch.HandoffKind)}; target_transport={FormatFileTransferTransportKindForLog(pendingEpoch.TargetTransport)}; proof={SanitizeLogToken(proof)}; proof_direction={SanitizeLogToken(proofDirection)}; proof_age_ms={proofAgeMs}");
                return true;
            }

            if (TryGetRuntimeUnlockRetryAuthorityForUnresolvedV6Epoch(
                    pendingEpoch,
                    reason,
                    stage,
                    out var authorityState))
            {
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_retry_allowed_current_recovery_contract_authority; reason={SanitizeLogToken(reason)}; stage={SanitizeLogToken(stage)}; session_id={SanitizeLogToken(pendingEpoch.SessionId)}; transfer_id={SanitizeLogToken(pendingEpoch.TransferId)}; direction={pendingEpoch.Direction.ToString().ToLowerInvariant()}; transport_epoch={pendingEpoch.TransportEpoch}; state={FormatFileTransferV6TransportEpochStateForLog(pendingEpoch.State)}; handoff_kind={FormatFileTransferTransportHandoffKindForLog(pendingEpoch.HandoffKind)}; target_transport={FormatFileTransferTransportKindForLog(pendingEpoch.TargetTransport)}; contract_generation={authorityState.ContractGeneration}; authority_attempt={authorityState.AuthorityAttempt}; reason_detail=post_tuna_fallback_runtime_unlock_authority");
                return true;
            }

            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_retry_blocked_post_tuna_fallback_unresolved; reason={SanitizeLogToken(reason)}; stage={SanitizeLogToken(stage)}; session_id={SanitizeLogToken(pendingEpoch.SessionId)}; transfer_id={SanitizeLogToken(pendingEpoch.TransferId)}; direction={pendingEpoch.Direction.ToString().ToLowerInvariant()}; transport_epoch={pendingEpoch.TransportEpoch}; state={FormatFileTransferV6TransportEpochStateForLog(pendingEpoch.State)}; handoff_kind={FormatFileTransferTransportHandoffKindForLog(pendingEpoch.HandoffKind)}; target_transport={FormatFileTransferTransportKindForLog(pendingEpoch.TargetTransport)}");
            return false;
        }

        if (!IsRegularNknRecoveryEpoch(pendingEpoch))
        {
            return false;
        }

        if (IsRuntimeUnlockActivationRetryReason(reason) &&
            HasActivePostTunaFallbackFileTransferRouteHint(pendingEpoch.SessionId))
        {
            if (TryGetCurrentPostTunaFallbackObservedSendProbeProof(
                    pendingEpoch.SessionId,
                    out var proof,
                    out var proofDirection,
                    out var proofAgeMs))
            {
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_retry_allowed_post_tuna_fallback_current_authority; reason={SanitizeLogToken(reason)}; stage={SanitizeLogToken(stage)}; session_id={SanitizeLogToken(pendingEpoch.SessionId)}; transfer_id={SanitizeLogToken(pendingEpoch.TransferId)}; direction={pendingEpoch.Direction.ToString().ToLowerInvariant()}; transport_epoch={pendingEpoch.TransportEpoch}; state={FormatFileTransferV6TransportEpochStateForLog(pendingEpoch.State)}; handoff_kind={FormatFileTransferTransportHandoffKindForLog(pendingEpoch.HandoffKind)}; target_transport={FormatFileTransferTransportKindForLog(pendingEpoch.TargetTransport)}; proof={SanitizeLogToken(proof)}; proof_direction={SanitizeLogToken(proofDirection)}; proof_age_ms={proofAgeMs}");
                return true;
            }

            if (TryGetRuntimeUnlockRetryAuthorityForUnresolvedV6Epoch(
                    pendingEpoch,
                    reason,
                    stage,
                    out var authorityState))
            {
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_retry_allowed_current_recovery_contract_authority; reason={SanitizeLogToken(reason)}; stage={SanitizeLogToken(stage)}; session_id={SanitizeLogToken(pendingEpoch.SessionId)}; transfer_id={SanitizeLogToken(pendingEpoch.TransferId)}; direction={pendingEpoch.Direction.ToString().ToLowerInvariant()}; transport_epoch={pendingEpoch.TransportEpoch}; state={FormatFileTransferV6TransportEpochStateForLog(pendingEpoch.State)}; handoff_kind={FormatFileTransferTransportHandoffKindForLog(pendingEpoch.HandoffKind)}; target_transport={FormatFileTransferTransportKindForLog(pendingEpoch.TargetTransport)}; contract_generation={authorityState.ContractGeneration}; authority_attempt={authorityState.AuthorityAttempt}; reason_detail=post_tuna_fallback_runtime_unlock_authority");
                return true;
            }

            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_retry_blocked_regular_nkn_recovery_for_post_tuna_fallback; reason={SanitizeLogToken(reason)}; stage={SanitizeLogToken(stage)}; session_id={SanitizeLogToken(pendingEpoch.SessionId)}; transfer_id={SanitizeLogToken(pendingEpoch.TransferId)}; direction={pendingEpoch.Direction.ToString().ToLowerInvariant()}; transport_epoch={pendingEpoch.TransportEpoch}; state={FormatFileTransferV6TransportEpochStateForLog(pendingEpoch.State)}; handoff_kind={FormatFileTransferTransportHandoffKindForLog(pendingEpoch.HandoffKind)}; target_transport={FormatFileTransferTransportKindForLog(pendingEpoch.TargetTransport)}");
            return false;
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_retry_allowed_regular_nkn_recovery_unresolved; reason={SanitizeLogToken(reason)}; stage={SanitizeLogToken(stage)}; session_id={SanitizeLogToken(pendingEpoch.SessionId)}; transfer_id={SanitizeLogToken(pendingEpoch.TransferId)}; direction={pendingEpoch.Direction.ToString().ToLowerInvariant()}; transport_epoch={pendingEpoch.TransportEpoch}; state={FormatFileTransferV6TransportEpochStateForLog(pendingEpoch.State)}; handoff_kind={FormatFileTransferTransportHandoffKindForLog(pendingEpoch.HandoffKind)}; target_transport={FormatFileTransferTransportKindForLog(pendingEpoch.TargetTransport)}");
        return true;
    }

    private bool TryGetRuntimeUnlockRetryAuthorityForUnresolvedV6Epoch(
        FileTransferV6TransportEpochSnapshot pendingEpoch,
        string reason,
        string stage,
        out RuntimeUnlockRecoveryRetryState authorityState)
    {
        authorityState = default!;
        if (!IsRuntimeUnlockActivationRetryReason(reason) ||
            string.IsNullOrWhiteSpace(pendingEpoch.SessionId) ||
            !HasActivePostTunaFallbackFileTransferRouteHint(pendingEpoch.SessionId))
        {
            return false;
        }

        var currentSessionId = currentSessionSecurityState.SessionId?.Value;
        if (string.IsNullOrWhiteSpace(currentSessionId) ||
            !string.Equals(currentSessionId.Trim(), pendingEpoch.SessionId.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryGetRuntimeUnlockRetryAuthorityForCurrentOffer(out var state) ||
            state is not { RetryAuthorityGranted: true, RetryAuthorityPending: true })
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(state.TransferId) &&
            !string.IsNullOrWhiteSpace(pendingEpoch.TransferId) &&
            !string.Equals(state.TransferId.Trim(), pendingEpoch.TransferId.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        authorityState = state;
        return true;
    }

    private bool ShouldAllowRuntimeUnlockRetryForActivePostTunaFallbackRepair(
        FileTransferV6TransportEpochSnapshot pendingEpoch)
    {
        var currentSessionId = currentSessionSecurityState.SessionId?.Value;
        if (string.IsNullOrWhiteSpace(currentSessionId) ||
            !string.Equals(currentSessionId.Trim(), pendingEpoch.SessionId, StringComparison.Ordinal))
        {
            return false;
        }

        lock (accelerationGate)
        {
            if (!TryGetCurrentTunaFallbackProofStateUnsafe(pendingEpoch.SessionId, out var state) ||
                (state.Lanes & NknAccelerationLaneKind.File) != NknAccelerationLaneKind.File ||
                state.FileState is TunaFallbackLaneState.None or TunaFallbackLaneState.Recovered)
            {
                return false;
            }

            if (state.FileV6TransportEpoch > 0 &&
                pendingEpoch.TransportEpoch > 0 &&
                state.FileV6TransportEpoch != pendingEpoch.TransportEpoch)
            {
                return false;
            }

            return true;
        }
    }

    private bool ShouldAllowAccelerationRetryDespiteFallbackControlProofPending(
        string? pendingSessionId,
        string pendingReason,
        NknAccelerationLaneKind pendingLanes,
        string retryReason,
        string stage)
    {
        if ((pendingLanes & NknAccelerationLaneKind.File) != NknAccelerationLaneKind.File ||
            !IsRuntimeUnlockActivationRetryReason(retryReason))
        {
            return false;
        }

        var currentSessionId = currentSessionSecurityState.SessionId?.Value;
        var normalizedPendingSessionId = string.IsNullOrWhiteSpace(pendingSessionId)
            ? currentSessionId
            : pendingSessionId.Trim();
        if (string.IsNullOrWhiteSpace(currentSessionId) ||
            string.IsNullOrWhiteSpace(normalizedPendingSessionId) ||
            !string.Equals(currentSessionId, normalizedPendingSessionId, StringComparison.Ordinal))
        {
            return false;
        }

        if (IsRuntimeUnlockActivationRecoveryFailure(pendingReason) &&
            HasActiveRegularV4FileTransferRouteHint(normalizedPendingSessionId))
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_retry_allowed_runtime_unlock_recovery_unproven; reason={SanitizeLogToken(retryReason)}; fallback_reason={SanitizeLogToken(pendingReason)}; stage={SanitizeLogToken(stage)}; session_id={SanitizeLogToken(normalizedPendingSessionId)}; lanes={FormatAccelerationLanesForLog(pendingLanes)}; active_regular_v4_route=1");
            return true;
        }

        if (HasActivePostTunaFallbackFileTransferRouteHint(normalizedPendingSessionId))
        {
            if ((string.Equals(
                 SanitizeLogToken(retryReason),
                 RuntimeUnlockCutThroughRetryReason,
                 StringComparison.Ordinal) ||
             IsRuntimeUnlockSidecarDropListenerRearmRetryReason(retryReason)) &&
                HasRuntimeUnlockRecoveryContractRequiringLocalListenerRetryForControlProofPreflight(
                    normalizedPendingSessionId))
            {
                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_retry_allowed_fallback_control_unproven_for_listener_rearm; reason={SanitizeLogToken(retryReason)}; fallback_reason={SanitizeLogToken(pendingReason)}; stage={SanitizeLogToken(stage)}; session_id={SanitizeLogToken(normalizedPendingSessionId)}; lanes={FormatAccelerationLanesForLog(pendingLanes)}; reason_detail=listener_rearm_must_precede_observed_send_probe");
                return true;
            }

            if (HasRuntimeUnlockRecoveryContractCompletedPeerResponseListenerRearmForPostTunaFallback(
                    normalizedPendingSessionId))
            {
                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_retry_allowed_fallback_control_unproven_after_listener_rearm; reason={SanitizeLogToken(retryReason)}; fallback_reason={SanitizeLogToken(pendingReason)}; stage={SanitizeLogToken(stage)}; session_id={SanitizeLogToken(normalizedPendingSessionId)}; lanes={FormatAccelerationLanesForLog(pendingLanes)}; reason_detail=listener_rearm_completed_must_dispatch_fresh_offer");
                return true;
            }

            if (TryGetCurrentPostTunaFallbackObservedSendProbeProof(
                    normalizedPendingSessionId,
                    out var proof,
                    out var proofDirection,
                    out var proofAgeMs))
            {
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_retry_allowed_post_tuna_fallback_current_authority; reason={SanitizeLogToken(retryReason)}; fallback_reason={SanitizeLogToken(pendingReason)}; stage={SanitizeLogToken(stage)}; session_id={SanitizeLogToken(normalizedPendingSessionId)}; lanes={FormatAccelerationLanesForLog(pendingLanes)}; proof={SanitizeLogToken(proof)}; proof_direction={SanitizeLogToken(proofDirection)}; proof_age_ms={proofAgeMs}");
                return true;
            }

            if (HasSettledRuntimeUnlockPostTunaFallbackAuthorityProbeCandidate(normalizedPendingSessionId))
            {
                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_retry_allowed_post_tuna_fallback_final_probe; reason={SanitizeLogToken(retryReason)}; fallback_reason={SanitizeLogToken(pendingReason)}; stage={SanitizeLogToken(stage)}; session_id={SanitizeLogToken(normalizedPendingSessionId)}; lanes={FormatAccelerationLanesForLog(pendingLanes)}; probe_reason={RuntimeUnlockPostTunaFallbackFinalProbeReason}");
                return true;
            }

            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_retry_blocked_fallback_control_unproven_for_post_tuna_fallback; reason={SanitizeLogToken(retryReason)}; fallback_reason={SanitizeLogToken(pendingReason)}; stage={SanitizeLogToken(stage)}; session_id={SanitizeLogToken(normalizedPendingSessionId)}; lanes={FormatAccelerationLanesForLog(pendingLanes)}");
            return false;
        }

        TunaFallbackLaneState fileState;
        V6TransportEpochState? fileV6EpochState;
        long fileV6TransportEpoch;
        long fallbackEpoch;
        lock (accelerationGate)
        {
            if (!TryGetCurrentTunaFallbackProofStateUnsafe(normalizedPendingSessionId, out var state) ||
                (state.Lanes & NknAccelerationLaneKind.File) != NknAccelerationLaneKind.File ||
                state.FileState == TunaFallbackLaneState.None)
            {
                return false;
            }

            fileState = state.FileState;
            fileV6EpochState = state.FileV6EpochState;
            fileV6TransportEpoch = state.FileV6TransportEpoch;
            fallbackEpoch = state.Epoch;
        }

        var fileV6EpochStateToken = fileV6EpochState is { } epochState
            ? FormatFileTransferV6TransportEpochStateForLog(epochState)
            : "none";
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_retry_allowed_fallback_control_unproven; reason={SanitizeLogToken(retryReason)}; fallback_reason={SanitizeLogToken(pendingReason)}; stage={SanitizeLogToken(stage)}; session_id={SanitizeLogToken(normalizedPendingSessionId)}; fallback_epoch={fallbackEpoch}; lanes={FormatAccelerationLanesForLog(pendingLanes)}; file_state={FormatTunaFallbackLaneState(fileState)}; file_v6_epoch_state={SanitizeLogToken(fileV6EpochStateToken)}; file_v6_transport_epoch={fileV6TransportEpoch}");
        return true;
    }

    private static bool IsSameSessionOrUnknown(string? candidateSessionId, string sessionId)
        => string.IsNullOrWhiteSpace(candidateSessionId) ||
           string.Equals(candidateSessionId.Trim(), sessionId.Trim(), StringComparison.Ordinal);

    private void RejectAccelerationOfferPreflight(
        string trigger,
        string reason,
        bool retryable,
        NknAccelerationLaneKind eligibleLanes = NknAccelerationLaneKind.None)
    {
        var normalizedReason = SanitizeLogToken(reason);
        var normalizedTrigger = SanitizeLogToken(trigger);
        var shouldRetry = retryable && ShouldRetryAccelerationOfferPreflight(normalizedTrigger, normalizedReason);
        var sessionId = currentSessionSecurityState.SessionId?.Value;
        var hasRemoteEndpoint = !string.IsNullOrWhiteSpace(remoteEndpoint);
        var canOfferListener = accelerationLane is INknTunaAccelerationSession tunaSession && tunaSession.CanOfferListener;

        NotifyTransportAccelerationStateChanged($"preflight_{normalizedReason}");
        LocalOperationalLog.Warn(
            "NKN.Tuna",
            $"event=tuna_acceleration_offer_preflight_rejected; reason={normalizedReason}; trigger={normalizedTrigger}; retryable={(retryable ? 1 : 0)}; retry_scheduled={(shouldRetry ? 1 : 0)}; session_id={SanitizeLogToken(sessionId)}; has_remote_endpoint={(hasRemoteEndpoint ? 1 : 0)}; can_offer_listener={(canOfferListener ? 1 : 0)}; eligible_lanes={FormatAccelerationLanesForLog(eligibleLanes)}");

        if (shouldRetry)
        {
            var retryReason = IsRuntimeUnlockNegotiationReason(normalizedTrigger)
                ? $"runtime_unlock_preflight_{normalizedReason}"
                : $"preflight_{normalizedReason}";
            ScheduleAccelerationNegotiationRetry(retryReason);
        }
    }

    private static bool ShouldRetryAccelerationOfferPreflight(string trigger, string reason)
    {
        if (string.Equals(trigger, "session_security_state_ready", StringComparison.Ordinal))
        {
            return string.Equals(reason, "listener_unavailable", StringComparison.Ordinal);
        }

        if (!IsRuntimeUnlockNegotiationReason(trigger) &&
            !trigger.StartsWith("retry_preflight_", StringComparison.Ordinal) &&
            !trigger.StartsWith("retry_early_drop_", StringComparison.Ordinal) &&
            !trigger.StartsWith("retry_sidecar_", StringComparison.Ordinal) &&
            !string.Equals(trigger, "helpee_payer_preferred", StringComparison.Ordinal))
        {
            return false;
        }

        return reason is "missing_remote_endpoint" or
            "listener_unavailable" or
            "missing_secure_session_context";
    }

    private void ScheduleAccelerationEarlyDropRetry(string reason, NknAccelerationLaneDiagnostics diagnostics)
    {
        var attempt = Interlocked.Increment(ref accelerationEarlyDropRetryAttempts);
        if (attempt > AccelerationEarlyDropMaxRetryAttempts)
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_acceleration_early_drop_retry_skipped; reason={SanitizeLogToken(reason)}; attempts={attempt - 1}; frame_count={TunaPayloadFrameCount(diagnostics)}");
            return;
        }

        LocalOperationalLog.Warn(
            "NKN.Tuna",
            $"event=tuna_acceleration_early_drop_retry_scheduled; reason={SanitizeLogToken(reason)}; attempt={attempt}; frame_count={TunaPayloadFrameCount(diagnostics)}; terminal_reason={SanitizeLogToken(diagnostics.TerminalSidecarReason)}");
        ScheduleAccelerationNegotiationRetry($"early_drop_{SanitizeLogToken(reason)}");
    }

    private static bool ShouldRetryEarlyAccelerationDrop(string? reason, NknAccelerationLaneDiagnostics diagnostics)
    {
        var normalized = SanitizeLogToken(reason);
        if (IsUserRequestedAccelerationStopReason(normalized) ||
            normalized.Contains("cap", StringComparison.Ordinal) ||
            normalized.Contains("queue_overflow", StringComparison.Ordinal))
        {
            return false;
        }

        if (TunaPayloadFrameCount(diagnostics) > 0)
        {
            return false;
        }

        return normalized is
            "read_failed" or
            "write_failed" or
            "remote_closed" or
            "sidecar_read_failed" or
            "sidecar_write_failed" or
            "sidecar_remote_closed" or
            "sidecar_local_ipc_eof" or
            "sidecar_tuna_stream_eof" or
            "sidecar_local_write_failed" or
            "sidecar_tuna_write_failed" or
            "sidecar_process_exited" or
            "sidecar_unexpected_exit";
    }

    private static long TunaPayloadFrameCount(NknAccelerationLaneDiagnostics diagnostics)
        => diagnostics.ControlFramesAccepted +
           diagnostics.MediaFramesAccepted +
           diagnostics.BulkFramesAccepted +
           diagnostics.ControlFramesWritten +
           diagnostics.MediaFramesWritten +
           diagnostics.BulkFramesWritten +
           diagnostics.ControlFramesReceived +
           diagnostics.MediaFramesReceived +
           diagnostics.BulkFramesReceived;

    private TimeSpan ResolveAccelerationOfferAnswerTimeout(string nonce)
    {
        if (AccelerationOfferAnswerTimeoutOverrideForTests is { } overrideTimeout)
        {
            return overrideTimeout;
        }

        lock (accelerationGate)
        {
            if (string.Equals(outboundAccelerationOfferNonce, nonce, StringComparison.Ordinal) &&
                IsRuntimeUnlockActivationReason(outboundAccelerationOfferTrigger))
            {
                return RuntimeUnlockAccelerationOfferAnswerTimeout;
            }

            var state = runtimeUnlockOfferProofState;
            if (state is { Retired: true } &&
                IsRuntimeUnlockPendingAnswerRetiredReason(state.RetiredReason) &&
                !string.IsNullOrWhiteSpace(retiredAccelerationOfferNonce) &&
                string.Equals(retiredAccelerationOfferNonce, nonce, StringComparison.Ordinal) &&
                string.Equals(retiredAccelerationOfferNonce, state.Nonce, StringComparison.Ordinal) &&
                string.Equals(retiredAccelerationOfferSessionId, state.SessionId, StringComparison.Ordinal) &&
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < retiredAccelerationOfferExpiresUtcMs)
            {
                return RuntimeUnlockAccelerationOfferAnswerTimeout;
            }
        }

        return AccelerationOfferAnswerTimeout;
    }

    private void ScheduleAccelerationOfferAnswerTimeout(string nonce)
    {
        var timeout = ResolveAccelerationOfferAnswerTimeout(nonce);
        if (timeout <= TimeSpan.Zero)
        {
            return;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(timeout, CancellationToken.None).ConfigureAwait(false);
                    if (disposed ||
                        IsAccelerationNegotiatedAndHealthy() ||
                        (!IsSessionAccelerationEligible(out _) &&
                         !HasPendingRuntimeUnlockAnswerTimeoutTarget(nonce)))
                    {
                        return;
                    }

                    string? offerTrigger;
                    long offerPayerDecisionId;
                    long offerGeneration;
                    var sessionId = currentSessionSecurityState.SessionId?.Value;
                    var isRuntimeUnlockOffer = false;
                    var runtimeUnlockPeerReceived = false;
                    var lateAnswerGrace = false;
                    var matchedRetiredPendingAnswer = false;
                    lock (accelerationGate)
                    {
                        var activeOfferMatches =
                            string.Equals(outboundAccelerationOfferNonce, nonce, StringComparison.Ordinal);
                        var state = runtimeUnlockOfferProofState;
                        var retiredPendingAnswerMatches =
                            !activeOfferMatches &&
                            state is { Retired: true } &&
                            IsRuntimeUnlockPendingAnswerRetiredReason(state.RetiredReason) &&
                            !string.IsNullOrWhiteSpace(retiredAccelerationOfferNonce) &&
                            string.Equals(retiredAccelerationOfferNonce, nonce, StringComparison.Ordinal) &&
                            string.Equals(retiredAccelerationOfferNonce, state.Nonce, StringComparison.Ordinal) &&
                            string.Equals(retiredAccelerationOfferSessionId, state.SessionId, StringComparison.Ordinal) &&
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < retiredAccelerationOfferExpiresUtcMs;
                        if (!activeOfferMatches && !retiredPendingAnswerMatches)
                        {
                            return;
                        }

                        matchedRetiredPendingAnswer = retiredPendingAnswerMatches;
                        offerTrigger = activeOfferMatches ? outboundAccelerationOfferTrigger : retiredAccelerationOfferTrigger;
                        offerPayerDecisionId = activeOfferMatches
                            ? outboundAccelerationOfferPayerDecisionId
                            : retiredAccelerationOfferPayerDecisionId;
                        offerGeneration = activeOfferMatches
                            ? outboundAccelerationOfferGeneration
                            : state?.Generation ?? 0;
                        isRuntimeUnlockOffer = IsRuntimeUnlockActivationReason(offerTrigger);
                        if (state is not null &&
                            state.Generation == offerGeneration &&
                            state.PayerDecisionId == offerPayerDecisionId &&
                            string.Equals(state.Nonce, nonce, StringComparison.Ordinal))
                        {
                            runtimeUnlockPeerReceived = state.PeerReceived;
                            state.Retired = true;
                            state.RetiredReason = "offer_answer_timeout";
                            state.RetryReason = IsRuntimeUnlockActivationReason(offerTrigger)
                                ? "runtime_unlock_offer_answer_timeout"
                                : "offer_answer_timeout";
                            lateAnswerGrace = false;
                            if (lateAnswerGrace && string.IsNullOrWhiteSpace(sessionId))
                            {
                                sessionId = state.SessionId;
                            }
                        }

                        if (lateAnswerGrace)
                        {
                            if (matchedRetiredPendingAnswer)
                            {
                                retiredAccelerationOfferExpiresUtcMs = DateTimeOffset.UtcNow
                                    .Add(timeout)
                                    .ToUnixTimeMilliseconds();
                            }
                            else
                            {
                                RetireOutboundAccelerationOfferLocked(
                                    sessionId ?? string.Empty,
                                    "offer_answer_timeout_late_answer_grace");
                            }
                        }
                        else if (matchedRetiredPendingAnswer)
                        {
                            ClearRetiredOutboundAccelerationOfferLocked();
                        }
                        else if (activeOfferMatches &&
                                 IsRuntimeUnlockActivationReason(offerTrigger))
                        {
                            RetireOutboundAccelerationOfferLocked(
                                sessionId ?? state?.SessionId ?? string.Empty,
                                "offer_answer_timeout");
                        }
                        else
                        {
                            outboundAccelerationOfferNonce = null;
                            outboundAccelerationOfferTrigger = null;
                            outboundAccelerationOfferPayerDecisionId = 0;
                        }
                    }

                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_offer_answer_timeout; timeout_ms={(int)timeout.TotalMilliseconds}");
                    if (lateAnswerGrace)
                    {
                        LocalOperationalLog.Info(
                            "NKN.Tuna",
                            $"event=tuna_acceleration_offer_answer_timeout_late_answer_grace; session_id={SanitizeLogToken(sessionId ?? "none")}; payer_decision_id={offerPayerDecisionId}; generation={offerGeneration}");
                    }
                    NotifyTransportAccelerationStateChanged("offer_answer_timeout");
                    ResumeFileTransferDataSessionsAfterTunaActivationFailure(
                        "offer_answer_timeout",
                        sessionId,
                        "offer_answer_timeout");
                    if (isRuntimeUnlockOffer)
                    {
                        var peerResponseMissing = !runtimeUnlockPeerReceived;
                        var retryReason = peerResponseMissing
                            ? RuntimeUnlockCutThroughRetryReason
                            : "runtime_unlock_offer_answer_timeout";
                        var listenerRetryReason = retryReason;
                        var recoveryRequested = TryRequestFileTransferTunaActivationOfferSendRecovery(
                            "offer_answer",
                            sessionId,
                            "answer_timeout_without_peer_response",
                            out var recoveryReason,
                            out var recoverySessionId);
                        var effectiveRecoveryReason = peerResponseMissing
                            ? RuntimeUnlockCutThroughRecoveryReason
                            : recoveryReason ?? "tuna_activation_offer_answer_timeout";
                        LocalOperationalLog.Warn(
                            "NKN.Tuna",
                            $"event=tuna_acceleration_activation_offer_not_observed; trigger={SanitizeLogToken(offerTrigger)}; session_id={SanitizeLogToken(sessionId ?? "none")}; payer_decision_id={offerPayerDecisionId}; generation={offerGeneration}; interruption_reason=offer_answer_timeout; retry_scheduled={(recoveryRequested ? 0 : 1)}; retry_after_recovery_armed={(recoveryRequested ? 1 : 0)}; replay_scheduled=0; answer_timeout_scheduled=0; recovery_requested={(recoveryRequested ? 1 : 0)}; recovery_reason={SanitizeLogToken(effectiveRecoveryReason)}; peer_received={(runtimeUnlockPeerReceived ? 1 : 0)}");
                        var listenerReadyReuse = peerResponseMissing &&
                            ShouldUseListenerReadyFastRetry(RuntimeUnlockCutThroughRetryReason);
                        if (recoveryRequested)
                        {
                            if (listenerReadyReuse)
                            {
                                LocalOperationalLog.Info(
                                    "NKN.Tuna",
                                    $"event=session_recovery_contract_listener_rearm_skipped; session_id={SanitizeLogToken(sessionId ?? "none")}; reason={SanitizeLogToken(listenerRetryReason)}; trigger=offer_answer_timeout; payer_decision_id={offerPayerDecisionId}; listener_ready_reuse=1");
                            }
                            else
                            {
                                LocalOperationalLog.Warn(
                                    "NKN.Tuna",
                                    $"event=session_recovery_contract_listener_rearm_required; session_id={SanitizeLogToken(sessionId ?? "none")}; reason={SanitizeLogToken(listenerRetryReason)}; trigger=offer_answer_timeout; payer_decision_id={offerPayerDecisionId}; listener_ready_reuse=0");
                                ScheduleAccelerationLaneStop(listenerRetryReason);
                            }

                            ArmRuntimeUnlockRetryAfterRecovery(
                                offerGeneration,
                                recoverySessionId ?? sessionId ?? string.Empty,
                                retryReason,
                                effectiveRecoveryReason,
                                requiresLocalListenerRetry: !listenerReadyReuse);
                        }
                        else
                        {
                            LocalOperationalLog.Warn(
                                "NKN.Tuna",
                                $"event=session_recovery_contract_listener_rearm_required; session_id={SanitizeLogToken(sessionId ?? "none")}; reason={SanitizeLogToken(listenerRetryReason)}; trigger=offer_answer_timeout; payer_decision_id={offerPayerDecisionId}; listener_ready_reuse=0");
                            ScheduleAccelerationLaneStop(listenerRetryReason);
                            ScheduleAccelerationNegotiationRetry(retryReason);
                        }
                    }
                    else
                    {
                        ScheduleAccelerationNegotiationRetry("offer_answer_timeout");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_offer_answer_timeout_failed; error={ex.GetType().Name}");
                }
            },
            CancellationToken.None);
    }

    private static bool ShouldRetryAccelerationNegotiation(string? reason)
        => string.Equals(reason, "sidecar_unavailable", StringComparison.Ordinal) ||
           string.Equals(reason, "listener_sidecar_unavailable", StringComparison.Ordinal) ||
           string.Equals(reason, "offer_queue_rejected", StringComparison.Ordinal) ||
           string.Equals(reason, "offer_answer_timeout", StringComparison.Ordinal) ||
           string.Equals(reason, "answer_ack_timeout", StringComparison.Ordinal) ||
           string.Equals(reason, "session_not_eligible", StringComparison.Ordinal);

    private static bool ShouldRetryPeerUserStoppedAfterRuntimeUnlock(string? reason, string? trigger)
        => string.Equals(SanitizeLogToken(reason), "user_stopped_tuna", StringComparison.Ordinal) &&
           IsRuntimeUnlockNegotiationReason(trigger);

    private static bool ShouldRetryRuntimeUnlockAfterRejectedAnswer(string? reason, string? trigger)
    {
        var normalizedReason = SanitizeLogToken(reason);
        return IsRuntimeUnlockActivationReason(trigger) &&
               normalizedReason is "sidecar_unavailable" or "listener_sidecar_unavailable";
    }

    private bool ShouldUseListenerReadyFastRetry(string? reason)
    {
        var normalized = SanitizeLogToken(reason);
        if (IsRuntimeUnlockListenerRearmRequiredRetryReason(normalized) &&
            !CanReuseCurrentListenerForRuntimeUnlockAnswerRetry(normalized))
        {
            return false;
        }

        if (normalized is not ("sidecar_unavailable" or "offer_answer_timeout" or "offer_queue_rejected" or "peer_user_stopped_tuna" or "preflight_listener_unavailable" or "early_drop_remote_closed") &&
            !IsRuntimeUnlockActivationRetryReason(normalized))
        {
            return false;
        }

        return accelerationLane is INknTunaAccelerationSession tunaSession &&
               tunaSession.CanOfferListener &&
               tunaSession.IsAvailable &&
               !string.IsNullOrWhiteSpace(tunaSession.LocalTunaAddress);
    }

    private bool CanReuseCurrentListenerForRuntimeUnlockAnswerRetry(string reason)
    {
        if (HasFailedRuntimeUnlockTunaPathLeaseForCurrentSession(currentSessionSecurityState.SessionId?.Value))
        {
            return false;
        }

        return (reason is "runtime_unlock_sidecar_unavailable" or "runtime_unlock_listener_sidecar_unavailable") &&
               accelerationLane is INknTunaAccelerationSession tunaSession &&
               tunaSession.CanOfferListener &&
               tunaSession.IsAvailable &&
               !string.IsNullOrWhiteSpace(tunaSession.LocalTunaAddress);
    }

    private static bool IsRuntimeUnlockListenerRearmRequiredRetryReason(string? reason)
    {
        var normalized = SanitizeLogToken(reason);
        return normalized is "runtime_unlock_sidecar_unavailable" or
            "runtime_unlock_listener_sidecar_unavailable" or
            "runtime_unlock_offer_answer_timeout" or
            "runtime_unlock_answer_ack_timeout";
    }

    private static bool IsRuntimeUnlockSidecarDropListenerRearmRetryReason(string? reason)
        => string.Equals(
            SanitizeLogToken(reason),
            "runtime_unlock_listener_sidecar_unavailable",
            StringComparison.Ordinal);

    private static bool ShouldNotifyRemoteAccelerationDown(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        return reason.Trim() switch
        {
            "read_failed" or
            "write_failed" or
            "remote_closed" or
            "queue_overflow" or
            "status_timeout" or
            "invalid_status" or
            "status_parse_failed" => true,
            _ => false,
        };
    }

    private bool TryCaptureAccelerationNegotiation(out string sessionId, out NknAccelerationLaneKind lanes)
    {
        lock (accelerationGate)
        {
            sessionId = accelerationSessionId ?? string.Empty;
            lanes = accelerationNegotiatedLanes;
        }

        return lanes != NknAccelerationLaneKind.None &&
               !string.IsNullOrWhiteSpace(sessionId) &&
               string.Equals(sessionId, currentSessionSecurityState.SessionId?.Value, StringComparison.Ordinal);
    }

    private void ScheduleAccelerationDownNotification(string sessionId, NknAccelerationLaneKind lanes, string reason)
    {
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await SendAccelerationDownAsync(sessionId, lanes, reason, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_down_notify_failed; reason={SanitizeLogToken(reason)}; error={ex.GetType().Name}");
                }
            },
            CancellationToken.None);
    }

    private async Task SendAccelerationDownAsync(
        string sessionId,
        NknAccelerationLaneKind lanes,
        string reason,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(remoteEndpoint) ||
            string.IsNullOrWhiteSpace(sessionId) ||
            !string.Equals(sessionId.Trim(), currentSessionSecurityState.SessionId?.Value, StringComparison.Ordinal) ||
            !TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            return;
        }

        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var down = new TransportAccelerationDownPayload
        {
            SessionId = sessionId.Trim(),
            SupportedLanes = NknAccelerationLaneCodec.ToNames(lanes),
            SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Nonce = nonce,
            SidecarProtocolVersion = TunaSidecarProtocolVersion,
            Reason = reason,
            PayerDecisionId = Volatile.Read(ref accelerationPayerDecisionId),
        };
        var payload = CreateSecureControlPayload(
            MsgType.TransportAccelerationDown,
            nonce,
            JsonSerializer.SerializeToUtf8Bytes(down));
        var envelope = CreateEnvelope(envelopeCode, MsgType.TransportAccelerationDown, payload, replyTo: null);
        var queued = await QueueControlEnvelopeAsync(remoteEndpoint, envelope, ControlOutboundLane.High, ct).ConfigureAwait(false);
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_down_notify_{(queued ? "queued" : "rejected")}; reason={SanitizeLogToken(reason)}; lanes={string.Join(",", down.SupportedLanes)}; payer_decision_id={down.PayerDecisionId}");
    }

    private bool TryGetPendingRuntimeUnlockAnswerAckForOffer(
        TransportAccelerationOfferPayload offer,
        out long generation,
        out NknAccelerationLaneKind lanes,
        out bool matchesPendingOffer)
    {
        generation = 0;
        lanes = NknAccelerationLaneKind.None;
        matchesPendingOffer = false;

        var sessionId = string.IsNullOrWhiteSpace(offer.SessionId)
            ? currentSessionSecurityState.SessionId?.Value ?? string.Empty
            : offer.SessionId.Trim();
        var nonce = string.IsNullOrWhiteSpace(offer.Nonce) ? string.Empty : offer.Nonce.Trim();
        if (string.IsNullOrWhiteSpace(sessionId) ||
            string.IsNullOrWhiteSpace(nonce))
        {
            return false;
        }

        lock (accelerationGate)
        {
            if (string.IsNullOrWhiteSpace(pendingAccelerationAnswerAckNonce) ||
                !IsRuntimeUnlockActivationReason(pendingAccelerationAnswerAckTrigger) ||
                !string.Equals(pendingAccelerationAnswerAckSessionId, sessionId, StringComparison.Ordinal))
            {
                return false;
            }

            generation = pendingAccelerationAnswerAckGeneration;
            lanes = pendingAccelerationAnswerAckLanes;
            matchesPendingOffer =
                string.Equals(pendingAccelerationAnswerAckNonce, nonce, StringComparison.Ordinal) &&
                pendingAccelerationAnswerAckPayerDecisionId == offer.PayerDecisionId;
            return true;
        }
    }

    private AccelerationValidationResult ValidateAccelerationOffer(
        string source,
        TransportAccelerationOfferPayload offer)
    {
        if (!TryGetAccelerationSessionCapabilityLanes(out var capabilityLanes))
        {
            return AccelerationValidationResult.HardReject("session_not_eligible");
        }

        var expectedSessionId = currentSessionSecurityState.SessionId?.Value;
        if (string.IsNullOrWhiteSpace(offer.SessionId) ||
            !string.Equals(offer.SessionId.Trim(), expectedSessionId, StringComparison.Ordinal))
        {
            return AccelerationValidationResult.HardReject("session_id_mismatch");
        }

        var expectedSource = ResolveExpectedRemotePeerAddressForCurrentSession();
        if (!AddressMatchesForSessionPolicy(source, expectedSource))
        {
            return AccelerationValidationResult.HardReject("source_identity_mismatch");
        }

        if (!IsAccelerationNonceValid(offer.Nonce))
        {
            return AccelerationValidationResult.HardReject("nonce_invalid");
        }

        if (offer.SidecarProtocolVersion != TunaSidecarProtocolVersion)
        {
            return AccelerationValidationResult.HardReject("sidecar_app_protocol_mismatch");
        }

        var allowsDelayedStaleOffer = false;
        if (!TryObserveRemotePayerDecision(offer.PayerDecisionId, "offer"))
        {
            allowsDelayedStaleOffer = ShouldAcceptDelayedHelpeeOfferDespiteStalePayerDecision(offer);
            if (!allowsDelayedStaleOffer)
            {
                return AccelerationValidationResult.HardReject("stale_payer_decision");
            }

            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_delayed_helpee_offer_stale_payer_decision_allowed; payer_decision_id={offer.PayerDecisionId}; latest_payer_decision_id={Volatile.Read(ref remoteAccelerationPayerDecisionId)}");
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (offer.SentAtUnixMs > 0 &&
            (offer.SentAtUnixMs > nowMs + TimeSpan.FromSeconds(30).TotalMilliseconds ||
             nowMs - offer.SentAtUnixMs > AccelerationOfferLifetime.TotalMilliseconds))
        {
            return AccelerationValidationResult.HardReject("expired");
        }

        if (nowMs >= offer.ExpiresAtUnixMs)
        {
            return AccelerationValidationResult.HardReject("expired");
        }

        if (string.IsNullOrWhiteSpace(offer.TunaAddress))
        {
            return AccelerationValidationResult.HardReject("missing_tuna_address");
        }

        var acceptedLanes = NknAccelerationLaneCodec.FromNames(offer.SupportedLanes) & capabilityLanes & ResolveConfiguredAccelerationLanes();
        return acceptedLanes == NknAccelerationLaneKind.None
            ? AccelerationValidationResult.SoftReject("unsupported_lane")
            : AccelerationValidationResult.Valid(acceptedLanes, allowsDelayedStaleOffer);
    }

    private AccelerationValidationResult ValidateAccelerationPayerIntent(
        string source,
        TransportAccelerationPayerIntentPayload intent)
    {
        if (!TryGetAccelerationSessionCapabilityLanes(out var capabilityLanes))
        {
            return AccelerationValidationResult.HardReject("session_not_eligible");
        }

        var expectedSessionId = currentSessionSecurityState.SessionId?.Value;
        if (string.IsNullOrWhiteSpace(intent.SessionId) ||
            !string.Equals(intent.SessionId.Trim(), expectedSessionId, StringComparison.Ordinal))
        {
            return AccelerationValidationResult.HardReject("session_id_mismatch");
        }

        var expectedSource = ResolveExpectedRemotePeerAddressForCurrentSession();
        if (!AddressMatchesForSessionPolicy(source, expectedSource))
        {
            return AccelerationValidationResult.HardReject("source_identity_mismatch");
        }

        if (!IsAccelerationNonceValid(intent.Nonce))
        {
            return AccelerationValidationResult.HardReject("nonce_invalid");
        }

        if (intent.SidecarProtocolVersion != TunaSidecarProtocolVersion)
        {
            return AccelerationValidationResult.HardReject("sidecar_app_protocol_mismatch");
        }

        if (!TryObserveRemotePayerDecision(intent.PayerDecisionId, "payer_intent"))
        {
            return AccelerationValidationResult.HardReject("stale_payer_decision");
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (nowMs >= intent.ExpiresAtUnixMs ||
            intent.SentAtUnixMs <= 0 ||
            nowMs - intent.SentAtUnixMs > AccelerationOfferLifetime.TotalMilliseconds)
        {
            return AccelerationValidationResult.HardReject("expired");
        }

        var normalizedIntent = SanitizeLogToken(intent.Intent);
        if (normalizedIntent is not "will_listen" and not "dialer_only")
        {
            return AccelerationValidationResult.HardReject("invalid_intent");
        }

        var acceptedLanes = NknAccelerationLaneCodec.FromNames(intent.SupportedLanes) & capabilityLanes & ResolveConfiguredAccelerationLanes();
        return acceptedLanes == NknAccelerationLaneKind.None
            ? AccelerationValidationResult.SoftReject("unsupported_lane")
            : AccelerationValidationResult.Valid(acceptedLanes);
    }

    private AccelerationValidationResult ValidateAccelerationAnswer(
        string source,
        TransportAccelerationAnswerPayload answer,
        bool requireAcceptedLanes)
    {
        if (!TryGetAccelerationSessionCapabilityLanes(out var capabilityLanes))
        {
            return AccelerationValidationResult.HardReject("session_not_eligible");
        }

        var expectedSessionId = currentSessionSecurityState.SessionId?.Value;
        if (string.IsNullOrWhiteSpace(answer.SessionId) ||
            !string.Equals(answer.SessionId.Trim(), expectedSessionId, StringComparison.Ordinal))
        {
            return AccelerationValidationResult.HardReject("session_id_mismatch");
        }

        var expectedSource = ResolveExpectedRemotePeerAddressForCurrentSession();
        if (!AddressMatchesForSessionPolicy(source, expectedSource))
        {
            return AccelerationValidationResult.HardReject("source_identity_mismatch");
        }

        if (!IsAccelerationNonceValid(answer.Nonce))
        {
            return AccelerationValidationResult.HardReject("nonce_invalid");
        }

        if (answer.SidecarProtocolVersion != TunaSidecarProtocolVersion)
        {
            return AccelerationValidationResult.HardReject("sidecar_app_protocol_mismatch");
        }

        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= answer.ExpiresAtUnixMs)
        {
            return AccelerationValidationResult.HardReject("expired");
        }

        if (!requireAcceptedLanes)
        {
            return AccelerationValidationResult.Valid(NknAccelerationLaneKind.None);
        }

        var acceptedLanes = NknAccelerationLaneCodec.FromNames(answer.SupportedLanes) & capabilityLanes & ResolveConfiguredAccelerationLanes();
        return acceptedLanes == NknAccelerationLaneKind.None
            ? AccelerationValidationResult.SoftReject("unsupported_lane")
            : AccelerationValidationResult.Valid(acceptedLanes);
    }

    private AccelerationValidationResult ValidateAccelerationAnswerAck(
        string source,
        TransportAccelerationAnswerAckPayload ack)
    {
        if (!TryGetAccelerationSessionCapabilityLanes(out var capabilityLanes))
        {
            return AccelerationValidationResult.HardReject("session_not_eligible");
        }

        var expectedSessionId = currentSessionSecurityState.SessionId?.Value;
        if (string.IsNullOrWhiteSpace(ack.SessionId) ||
            !string.Equals(ack.SessionId.Trim(), expectedSessionId, StringComparison.Ordinal))
        {
            return AccelerationValidationResult.HardReject("session_id_mismatch");
        }

        var expectedSource = ResolveExpectedRemotePeerAddressForCurrentSession();
        if (!AddressMatchesForSessionPolicy(source, expectedSource))
        {
            return AccelerationValidationResult.HardReject("source_identity_mismatch");
        }

        if (!IsAccelerationNonceValid(ack.Nonce))
        {
            return AccelerationValidationResult.HardReject("nonce_invalid");
        }

        if (ack.SidecarProtocolVersion != TunaSidecarProtocolVersion)
        {
            return AccelerationValidationResult.HardReject("sidecar_app_protocol_mismatch");
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (nowMs >= ack.ExpiresAtUnixMs ||
            ack.SentAtUnixMs <= 0 ||
            nowMs - ack.SentAtUnixMs > AccelerationOfferLifetime.TotalMilliseconds)
        {
            return AccelerationValidationResult.HardReject("expired");
        }

        var acceptedLanes = NknAccelerationLaneCodec.FromNames(ack.SupportedLanes) & capabilityLanes & ResolveConfiguredAccelerationLanes();
        return acceptedLanes == NknAccelerationLaneKind.None
            ? AccelerationValidationResult.SoftReject("unsupported_lane")
            : AccelerationValidationResult.Valid(acceptedLanes);
    }

    private string? ValidateAccelerationDown(string source, TransportAccelerationDownPayload down)
    {
        if (!IsSessionAccelerationEligible(out _))
        {
            return "session_not_eligible";
        }

        var expectedSessionId = currentSessionSecurityState.SessionId?.Value;
        if (string.IsNullOrWhiteSpace(down.SessionId) ||
            !string.Equals(down.SessionId.Trim(), expectedSessionId, StringComparison.Ordinal))
        {
            return "session_id_mismatch";
        }

        var expectedSource = ResolveExpectedRemotePeerAddressForCurrentSession();
        if (!AddressMatchesForSessionPolicy(source, expectedSource))
        {
            return "source_identity_mismatch";
        }

        if (down.SidecarProtocolVersion != TunaSidecarProtocolVersion)
        {
            return "sidecar_app_protocol_mismatch";
        }

        if (!TryObserveRemotePayerDecision(down.PayerDecisionId, "down"))
        {
            return "stale_payer_decision";
        }

        if (!IsAccelerationNonceValid(down.Nonce))
        {
            return "nonce_invalid";
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (down.SentAtUnixMs <= 0 ||
            down.SentAtUnixMs > nowMs + TimeSpan.FromSeconds(30).TotalMilliseconds ||
            nowMs - down.SentAtUnixMs > AccelerationOfferLifetime.TotalMilliseconds)
        {
            return "stale";
        }

        return null;
    }

    private bool IsSessionAccelerationEligible(out NknAccelerationLaneKind eligibleLanes)
    {
        if (!TryGetAccelerationSessionCapabilityLanes(out var capabilityLanes))
        {
            eligibleLanes = NknAccelerationLaneKind.None;
            return false;
        }

        eligibleLanes = capabilityLanes & ResolveConfiguredAccelerationLanes();
        return eligibleLanes != NknAccelerationLaneKind.None;
    }

    private bool TryGetAccelerationSessionCapabilityLanes(out NknAccelerationLaneKind eligibleLanes)
    {
        eligibleLanes = NknAccelerationLaneKind.None;
        var state = currentSessionSecurityState;
        var nowUtc = DateTimeOffset.UtcNow;
        if (!state.InviteValidated ||
            !state.HandshakeCompleted ||
            state.HandshakeState != SessionHandshakeState.Verified ||
            !state.IsApprovalActive(nowUtc) ||
            state.SessionId is null)
        {
            return false;
        }

        if (state.HasCapability(CapabilityGrant.FileTransfer, nowUtc))
        {
            eligibleLanes |= NknAccelerationLaneKind.File;
        }

        if (state.HasCapability(CapabilityGrant.ScreenShare, nowUtc))
        {
            eligibleLanes |= NknAccelerationLaneKind.Screen;
        }

        return eligibleLanes != NknAccelerationLaneKind.None;
    }

    private static bool IsAccelerationNonceValid(string? nonce)
    {
        var trimmed = string.IsNullOrWhiteSpace(nonce) ? string.Empty : nonce.Trim();
        return trimmed.Length is > 0 and <= 128;
    }

    private NknAccelerationLaneKind ResolveConfiguredAccelerationLanes()
        => tunaAccelerationOptions.Enabled
            ? tunaAccelerationOptions.Lanes
            : accelerationLane is null
                ? NknAccelerationLaneKind.None
                : NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen;

    private bool IsAccelerationNegotiatedAndHealthy()
    {
        lock (accelerationGate)
        {
            return accelerationLane?.IsAvailable == true &&
                   accelerationNegotiatedLanes != NknAccelerationLaneKind.None &&
                   !string.IsNullOrWhiteSpace(accelerationSessionId) &&
                   string.Equals(accelerationSessionId, currentSessionSecurityState.SessionId?.Value, StringComparison.Ordinal);
        }
    }

    private async Task<bool> TrySendAcceleratedEnvelopeAsync(
        MsgType messageType,
        NknBridgeChannel channel,
        byte[] envelopeBytes,
        CancellationToken ct,
        bool allowDuringRegularNknFallback = false)
    {
        var lane = messageType switch
        {
            MsgType.ScreenShareFrame when channel == NknBridgeChannel.Media => NknAccelerationLaneKind.Screen,
            MsgType.FileTransferDataFrame when channel == NknBridgeChannel.Bulk => NknAccelerationLaneKind.File,
            _ => NknAccelerationLaneKind.None,
        };
        var laneClient = accelerationLane;
        if (lane == NknAccelerationLaneKind.None ||
            laneClient is null)
        {
            return false;
        }

        if (!allowDuringRegularNknFallback &&
            ShouldSuppressAcceleratedFileTransferBulkDuringRegularNknFallback(lane))
        {
            return false;
        }

        if (!IsAccelerationNegotiatedAndHealthy())
        {
            if (TryCaptureAccelerationNegotiation(out var unavailableSessionId, out var unavailableLanes))
            {
                StartTunaFallbackProofAndRebindIfNeeded("tuna_unavailable_before_send", unavailableSessionId, unavailableLanes);
            }

            return false;
        }

        lock (accelerationGate)
        {
            if ((accelerationNegotiatedLanes & lane) != lane)
            {
                return false;
            }
        }

        try
        {
            var sent = await laneClient.TrySendAsync(channel, envelopeBytes, ct).ConfigureAwait(false);
            if (sent)
            {
                if (!IsAccelerationNegotiatedAndHealthy())
                {
                    if (TryCaptureAccelerationNegotiation(out var invalidatedSessionId, out var invalidatedLanes))
                    {
                        StartTunaFallbackProofAndRebindIfNeeded("tuna_send_invalidated_after_queue", invalidatedSessionId, invalidatedLanes);
                    }

                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_accelerated_envelope_send_invalidated_after_queue; message_type={MapEnvelopeTypeForDiagnostics(messageType)}; channel={MapBridgeChannel(channel)}; payload_bytes={envelopeBytes.Length}");
                    return false;
                }

                MarkTunaFallbackAccelerationUsedAfterProof();
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=tuna_accelerated_envelope_sent; message_type={MapEnvelopeTypeForDiagnostics(messageType)}; channel={MapBridgeChannel(channel)}; payload_bytes={envelopeBytes.Length}");
                return true;
            }

            LogAcceleratedEnvelopeTrySendRejected(messageType, channel, lane, envelopeBytes, laneClient.GetDiagnosticsSnapshot());
            if (TryCaptureAccelerationNegotiation(out var rejectedSessionId, out var rejectedLanes))
            {
                StartTunaFallbackProofAndRebindIfNeeded("tuna_send_rejected", rejectedSessionId, rejectedLanes);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_accelerated_envelope_send_failed; message_type={MapEnvelopeTypeForDiagnostics(messageType)}; channel={MapBridgeChannel(channel)}; error={ex.GetType().Name}");
            if (TryCaptureAccelerationNegotiation(out var failedSessionId, out var failedLanes))
            {
                StartTunaFallbackProofAndRebindIfNeeded("tuna_send_failed", failedSessionId, failedLanes);
            }
        }

        return false;
    }

    private static void LogAcceleratedEnvelopeTrySendRejected(
        MsgType messageType,
        NknBridgeChannel channel,
        NknAccelerationLaneKind lane,
        byte[] envelopeBytes,
        NknAccelerationLaneDiagnostics diagnostics)
    {
        LocalOperationalLog.Warn(
            "NKN.Tuna",
            "event=tuna_accelerated_envelope_try_send_returned_false" +
            $"; message_type={MapEnvelopeTypeForDiagnostics(messageType)}" +
            $"; channel={MapBridgeChannel(channel)}" +
            $"; lane={FormatAccelerationLanesForLog(lane)}" +
            $"; payload_bytes={Math.Max(0, envelopeBytes?.Length ?? 0)}" +
            $"; lane_available={(diagnostics.IsAvailable ? 1 : 0)}" +
            $"; last_unavailable_reason={SanitizeLogToken(diagnostics.LastUnavailableReason)}" +
            $"; terminal_sidecar_reason={SanitizeLogToken(diagnostics.TerminalSidecarReason)}" +
            $"; send_rejected={diagnostics.SendRejected}" +
            $"; queue_overflow={diagnostics.QueueOverflow}" +
            $"; control_accepted={diagnostics.ControlFramesAccepted}" +
            $"; control_written={diagnostics.ControlFramesWritten}" +
            $"; media_accepted={diagnostics.MediaFramesAccepted}" +
            $"; media_written={diagnostics.MediaFramesWritten}" +
            $"; bulk_accepted={diagnostics.BulkFramesAccepted}" +
            $"; bulk_written={diagnostics.BulkFramesWritten}");
    }

    private bool ShouldSuppressAcceleratedFileTransferBulkDuringRegularNknFallback(NknAccelerationLaneKind lane)
    {
        if (lane != NknAccelerationLaneKind.File)
        {
            return false;
        }

        TunaFallbackProofState? snapshot = null;
        var shouldLog = false;
        lock (accelerationGate)
        {
            if (!TryGetCurrentTunaFallbackProofStateUnsafe(currentSessionSecurityState.SessionId?.Value, out var state) ||
                (state.Lanes & NknAccelerationLaneKind.File) != NknAccelerationLaneKind.File ||
                state.FileState == TunaFallbackLaneState.None)
            {
                return false;
            }

            shouldLog = ShouldLogTunaFallbackProofMarkerUnsafe(
                state,
                "file_acceleration_suppressed_regular_nkn_fallback",
                DateTimeOffset.UtcNow);
            snapshot = state;
        }

        if (shouldLog && snapshot is not null)
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_file_acceleration_suppressed_regular_nkn_fallback; session_id={SanitizeLogToken(snapshot.SessionId)}; fallback_epoch={snapshot.Epoch}; reason={snapshot.Reason}; file_state={FormatTunaFallbackLaneState(snapshot.FileState)}; file_v6_epoch_state={FormatTunaFallbackFileV6EpochState(snapshot)}");
        }

        return true;
    }

    public Task RequestAccelerationNegotiationAsync(string reason, CancellationToken ct)
    {
        var isRuntimeUnlock = IsRuntimeUnlockNegotiationReason(reason);
        if (isRuntimeUnlock)
        {
            ClearAccelerationUserStoppedForCurrentSession();
            ClearAccelerationPeerUserStoppedForCurrentSession();
            Interlocked.Exchange(ref accelerationEarlyDropRetryAttempts, 0);
        }

        if (ct.IsCancellationRequested ||
            disposed ||
            accelerationLane is not INknTunaAccelerationSession ||
            IsAccelerationUserStoppedForCurrentSession() ||
            !IsSessionAccelerationEligible(out _))
        {
            return Task.CompletedTask;
        }

        if (isRuntimeUnlock &&
            TryRequestRuntimeUnlockFileTransferHandoffFromActiveFallback(reason))
        {
            return Task.CompletedTask;
        }

        if (IsAccelerationNegotiatedAndHealthy())
        {
            return Task.CompletedTask;
        }

        lock (accelerationGate)
        {
            if (!string.IsNullOrWhiteSpace(outboundAccelerationOfferNonce) ||
                !string.IsNullOrWhiteSpace(pendingAccelerationAnswerAckNonce))
            {
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_negotiation_suppressed_pending_generation; reason={SanitizeLogToken(reason)}; runtime_unlock={(isRuntimeUnlock ? 1 : 0)}; has_offer={(!string.IsNullOrWhiteSpace(outboundAccelerationOfferNonce) ? 1 : 0)}; has_answer_ack={(!string.IsNullOrWhiteSpace(pendingAccelerationAnswerAckNonce) ? 1 : 0)}");
                return Task.CompletedTask;
            }
        }

        if (isRuntimeUnlock)
        {
            NotifyTransportAccelerationStateChanged("renegotiating_after_user_unlock");
        }

        Interlocked.Exchange(ref accelerationNegotiationRetryAttempts, 0);
        ScheduleAccelerationNegotiationIfEligible(string.IsNullOrWhiteSpace(reason)
            ? "runtime_unlock"
            : reason.Trim());
        return Task.CompletedTask;
    }

    private bool TryRequestRuntimeUnlockFileTransferHandoffFromActiveFallback(string reason)
    {
        string? sessionId;
        NknAccelerationLaneKind lanes;
        TunaFallbackProofState? fallbackSnapshot;

        lock (accelerationGate)
        {
            sessionId = accelerationSessionId ?? currentSessionSecurityState.SessionId?.Value;
            if (!IsAccelerationNegotiatedAndHealthyUnsafe(sessionId) ||
                (accelerationNegotiatedLanes & NknAccelerationLaneKind.File) != NknAccelerationLaneKind.File ||
                tunaFallbackProofState is not { } state ||
                string.IsNullOrWhiteSpace(sessionId) ||
                !string.Equals(state.SessionId, sessionId, StringComparison.Ordinal) ||
                (state.Lanes & NknAccelerationLaneKind.File) != NknAccelerationLaneKind.File ||
                state.FileState == TunaFallbackLaneState.None)
            {
                return false;
            }

            lanes = accelerationNegotiatedLanes;
            fallbackSnapshot = state;
        }

        var normalizedReason = string.IsNullOrWhiteSpace(reason)
            ? "runtime_unlock"
            : SanitizeLogToken(reason);
        var previousFileV6EpochStateToken = fallbackSnapshot.FileV6EpochState is { } epochState
            ? FormatFileTransferV6TransportEpochStateForLog(epochState)
            : "none";
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_runtime_unlock_file_fallback_handoff_requested; session_id={SanitizeLogToken(sessionId)}; reason={normalizedReason}; lanes={FormatAccelerationLanesForLog(lanes)}; fallback_epoch={fallbackSnapshot.Epoch}; file_state={FormatTunaFallbackLaneState(fallbackSnapshot.FileState)}; file_v6_epoch_state={SanitizeLogToken(previousFileV6EpochStateToken)}; file_v6_transport_epoch={fallbackSnapshot.FileV6TransportEpoch}");

        RequestFileTransferTunaActivationHandoff(
            sessionId,
            lanes,
            normalizedReason);
        return true;
    }

    public async Task StopAccelerationAsync(string reason, CancellationToken ct)
    {
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? "user_locked" : SanitizeLogToken(reason);
        var shouldNotifyRemoteDown = TryCaptureAccelerationNegotiation(out var downSessionId, out var downLanes);
        if (shouldNotifyRemoteDown && IsUserRequestedAccelerationStopReason(normalizedReason))
        {
            MarkAccelerationUserStoppedForCurrentSession(downSessionId);
        }

        if (shouldNotifyRemoteDown)
        {
            ScheduleAccelerationDownNotification(downSessionId, downLanes, normalizedReason);
        }

        ResetAccelerationNegotiation(normalizedReason);
        if (accelerationLane is INknTunaAccelerationSession tunaSession)
        {
            try
            {
                await tunaSession.StopAsync(normalizedReason, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_stop_failed; reason={normalizedReason}; error={ex.GetType().Name}");
            }
        }
    }

    private void ScheduleAccelerationLaneStop(string reason)
    {
        if (accelerationLane is not INknTunaAccelerationSession tunaSession)
        {
            return;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await tunaSession.StopAsync(reason, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_stop_failed; reason={SanitizeLogToken(reason)}; error={ex.GetType().Name}");
                }
            },
            CancellationToken.None);
    }

    private void ResetAccelerationNegotiation(string reason)
    {
        var normalizedReason = SanitizeLogToken(reason);
        string? fallbackSessionId;
        NknAccelerationLaneKind fallbackLanes;
        bool preserveRuntimeUnlockPendingAnswer;
        bool retryUnobservedRuntimeUnlockOfferAfterReset;
        bool retryInterruptedRuntimeUnlockOfferAfterReset;
        string retryUnobservedRuntimeUnlockSessionId = string.Empty;
        long retryUnobservedRuntimeUnlockPayerDecisionId = 0;
        long retryUnobservedRuntimeUnlockGeneration = 0;
        string retryUnobservedRuntimeUnlockReason = string.Empty;
        string retryInterruptedRuntimeUnlockSessionId = string.Empty;
        long retryInterruptedRuntimeUnlockPayerDecisionId = 0;
        long retryInterruptedRuntimeUnlockGeneration = 0;
        string retryInterruptedRuntimeUnlockReason = string.Empty;
        lock (accelerationGate)
        {
            fallbackSessionId = accelerationSessionId;
            fallbackLanes = accelerationNegotiatedLanes;
            preserveRuntimeUnlockPendingAnswer =
                ShouldPreserveRuntimeUnlockPendingAnswerAcrossResetLocked(normalizedReason);
            retryUnobservedRuntimeUnlockOfferAfterReset =
                !preserveRuntimeUnlockPendingAnswer &&
                TryCaptureUnobservedRuntimeUnlockOfferResetRetryLocked(
                    normalizedReason,
                    out retryUnobservedRuntimeUnlockSessionId,
                    out retryUnobservedRuntimeUnlockPayerDecisionId,
                    out retryUnobservedRuntimeUnlockGeneration,
                    out retryUnobservedRuntimeUnlockReason);
            retryInterruptedRuntimeUnlockOfferAfterReset =
                !preserveRuntimeUnlockPendingAnswer &&
                !retryUnobservedRuntimeUnlockOfferAfterReset &&
                TryCaptureInterruptedRuntimeUnlockOfferResetRetryLocked(
                    normalizedReason,
                    out retryInterruptedRuntimeUnlockSessionId,
                    out retryInterruptedRuntimeUnlockPayerDecisionId,
                    out retryInterruptedRuntimeUnlockGeneration,
                    out retryInterruptedRuntimeUnlockReason);
            if (!preserveRuntimeUnlockPendingAnswer &&
                !retryInterruptedRuntimeUnlockOfferAfterReset)
            {
                ClearOutboundAccelerationOfferLocked();
                ClearRetiredOutboundAccelerationOfferLocked();
            }
            accelerationSessionId = null;
            accelerationNegotiatedLanes = NknAccelerationLaneKind.None;
            ClearPendingAccelerationAnswerAckLocked();
            if (ShouldResetRemotePayerDecisionForResetReason(reason))
            {
                remoteAccelerationPayerDecisionId = 0;
            }
        }

        var suppressFallbackProof = ShouldSuppressTunaFallbackProofAfterUserStop(normalizedReason, fallbackSessionId);
        var forceFileTransferFallbackAfterUserStop =
            suppressFallbackProof &&
            ShouldStartFileTransferFallbackAfterUserStop(normalizedReason, fallbackLanes);
        if (forceFileTransferFallbackAfterUserStop)
        {
            suppressFallbackProof = false;
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_user_stop_filetransfer_fallback_forced; reason={normalizedReason}; session_id={SanitizeLogToken(fallbackSessionId ?? "none")}; lanes={FormatAccelerationLanesForLog(fallbackLanes)}");
        }

        var effectiveFallbackLanes = forceFileTransferFallbackAfterUserStop
            ? fallbackLanes & NknAccelerationLaneKind.File
            : fallbackLanes;
        var shouldStartFallbackProof =
            effectiveFallbackLanes != NknAccelerationLaneKind.None &&
            (forceFileTransferFallbackAfterUserStop ||
             (!suppressFallbackProof && ShouldStartTunaFallbackProofForResetReason(normalizedReason)));
        if (shouldStartFallbackProof)
        {
            StartTunaFallbackProofIfNeeded(normalizedReason, fallbackSessionId, effectiveFallbackLanes);
            RebindFileTransferDataSessionsForTunaFallback(normalizedReason, fallbackSessionId, effectiveFallbackLanes);
            RebindScreenShareDataSessionsForTunaFallback(normalizedReason, fallbackSessionId, effectiveFallbackLanes);
        }
        else if (TryGetActiveFileTransferTunaActivationPauseForCurrentSession(out var pausedSessionId))
        {
            ResumeFileTransferDataSessionsAfterTunaActivationFailure(
                normalizedReason,
                pausedSessionId,
                "reset_acceleration");
        }
        else if (suppressFallbackProof)
        {
            CompleteTunaFallbackProof(normalizedReason);
            ResumeFileTransferDataSessionsAfterTunaActivationNegotiation(
                $"reset_{normalizedReason}",
                currentSessionSecurityState.SessionId?.Value,
                "reset_acceleration");
        }
        else if (ShouldCompleteTunaFallbackProofForResetReason(normalizedReason))
        {
            CompleteTunaFallbackProof(normalizedReason);
        }
        else
        {
            ResumeFileTransferDataSessionsAfterTunaActivationNegotiation(
                $"reset_{normalizedReason}",
                currentSessionSecurityState.SessionId?.Value,
                "reset_acceleration");
        }

        Interlocked.Exchange(ref accelerationNegotiationScheduled, 0);
        Interlocked.Exchange(ref pendingRuntimeUnlockAccelerationNegotiation, 0);
        Interlocked.Exchange(ref accelerationNegotiationRetryAttempts, 0);
        Interlocked.Exchange(ref helperPaidOfferPriorityDelayConsumed, 0);
        Interlocked.Exchange(ref remoteHelpeeAccelerationOfferObserved, 0);
        Interlocked.Exchange(ref remoteHelpeePayerIntentState, RemoteHelpeePayerIntentUnknown);
        Interlocked.Exchange(ref remoteHelpeePayerIntentObservedUtcMs, 0);
        AdvancePayerDecisionEpoch($"reset_{normalizedReason}");
        LocalOperationalLog.Info("NKN.Tuna", $"event=tuna_acceleration_reset; reason={normalizedReason}; fallback_proof_suppressed={(suppressFallbackProof ? 1 : 0)}");
        NotifyTransportAccelerationStateChanged(normalizedReason);
        if (retryUnobservedRuntimeUnlockOfferAfterReset)
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_runtime_unlock_unobserved_offer_reset_retry_scheduled; reason={normalizedReason}; session_id={SanitizeLogToken(retryUnobservedRuntimeUnlockSessionId)}; payer_decision_id={retryUnobservedRuntimeUnlockPayerDecisionId}; generation={retryUnobservedRuntimeUnlockGeneration}; retry_reason={SanitizeLogToken(retryUnobservedRuntimeUnlockReason)}");
            ScheduleAccelerationNegotiationRetry(retryUnobservedRuntimeUnlockReason);
        }

        if (retryInterruptedRuntimeUnlockOfferAfterReset)
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_runtime_unlock_interrupted_offer_reset_retry_scheduled; reason={normalizedReason}; session_id={SanitizeLogToken(retryInterruptedRuntimeUnlockSessionId)}; payer_decision_id={retryInterruptedRuntimeUnlockPayerDecisionId}; generation={retryInterruptedRuntimeUnlockGeneration}; retry_reason={SanitizeLogToken(retryInterruptedRuntimeUnlockReason)}");
            ScheduleAccelerationNegotiationRetry(retryInterruptedRuntimeUnlockReason);
        }
    }

    private void NotifyTransportAccelerationStateChanged(string reason)
    {
        var active = IsAccelerationNegotiatedAndHealthy();
        var activeValue = active ? 1 : 0;
        var previousActiveValue = Interlocked.Exchange(ref transportAccelerationActivePublished, activeValue);
        var normalizedReason = active
            ? GetActiveAccelerationStatusReason()
            : string.IsNullOrWhiteSpace(reason) ? "unknown" : SanitizeLogToken(reason);
        var reasonChanged = false;

        lock (accelerationGate)
        {
            if (!string.Equals(transportAccelerationStatusReason, normalizedReason, StringComparison.Ordinal))
            {
                transportAccelerationStatusReason = normalizedReason;
                reasonChanged = true;
            }
        }

        if (previousActiveValue == activeValue && !reasonChanged)
        {
            return;
        }

        string? sessionId;
        NknAccelerationLaneKind lanes;
        long payerDecisionId;
        lock (accelerationGate)
        {
            sessionId = accelerationSessionId ?? currentSessionSecurityState.SessionId?.Value;
            lanes = accelerationNegotiatedLanes;
            payerDecisionId = accelerationPayerDecisionId;
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_timeline; status={normalizedReason}; active={activeValue}; session_id={sessionId ?? "(none)"}; lanes={FormatAccelerationLanesForLog(lanes)}; payer_decision_id={payerDecisionId}");
        TransportAccelerationStateChanged?.Invoke(
            this,
            new TransportAccelerationStateChangedEventArgs(active, normalizedReason));
    }

    private string GetActiveAccelerationStatusReason()
    {
        var isLocalPaidListener = accelerationLane is INknTunaAccelerationSession { IsLocalPaidListenerActive: true };
        if (IsFileTransferUsingRegularNknFallbackForCurrentSession())
        {
            return isLocalPaidListener
                ? "paid_listener_active_file_regular_nkn_fallback"
                : "free_dialer_active_file_regular_nkn_fallback";
        }

        return isLocalPaidListener
            ? "paid_listener_active"
            : "free_dialer_active";
    }

    private bool IsFileTransferUsingRegularNknFallbackForCurrentSession()
    {
        var currentSessionId = currentSessionSecurityState.SessionId?.Value;
        if (string.IsNullOrWhiteSpace(currentSessionId))
        {
            return false;
        }

        lock (accelerationGate)
        {
            return tunaFallbackProofState is { } state &&
                   string.Equals(state.SessionId, currentSessionId, StringComparison.Ordinal) &&
                   (state.Lanes & NknAccelerationLaneKind.File) == NknAccelerationLaneKind.File &&
                   state.FileState != TunaFallbackLaneState.None;
        }
    }

    private bool ShouldUseFileTransferV6ForAccelerationCore()
    {
        if (IsPostTunaFileFallbackActiveForRouteSelection)
        {
            return true;
        }

        return false;
    }

    private bool IsFileTransferAccelerationNegotiatedAndHealthy()
    {
        var currentSessionId = currentSessionSecurityState.SessionId?.Value;
        lock (accelerationGate)
        {
            return IsAccelerationNegotiatedAndHealthyUnsafe(currentSessionId) &&
                   (accelerationNegotiatedLanes & NknAccelerationLaneKind.File) == NknAccelerationLaneKind.File;
        }
    }

    private bool IsAccelerationUserStoppedForCurrentSession()
    {
        var currentSessionId = currentSessionSecurityState.SessionId?.Value;
        lock (accelerationGate)
        {
            return !string.IsNullOrWhiteSpace(currentSessionId) &&
                   string.Equals(accelerationUserStoppedSessionId, currentSessionId, StringComparison.Ordinal);
        }
    }

    private void MarkAccelerationUserStoppedForCurrentSession(string? sessionId = null)
    {
        var stoppedSessionId = string.IsNullOrWhiteSpace(sessionId)
            ? currentSessionSecurityState.SessionId?.Value
            : sessionId.Trim();
        if (string.IsNullOrWhiteSpace(stoppedSessionId))
        {
            return;
        }

        lock (accelerationGate)
        {
            accelerationUserStoppedSessionId = stoppedSessionId;
            accelerationUserStoppedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }

    private void ClearAccelerationUserStoppedForCurrentSession()
    {
        var currentSessionId = currentSessionSecurityState.SessionId?.Value;
        lock (accelerationGate)
        {
            if (string.IsNullOrWhiteSpace(currentSessionId) ||
                string.Equals(accelerationUserStoppedSessionId, currentSessionId, StringComparison.Ordinal))
            {
                accelerationUserStoppedSessionId = null;
                accelerationUserStoppedUtcMs = 0;
            }
        }
    }

    private void ClearAccelerationUserStoppedForFreshPeerMessage(string messageType, string? trigger, long sentAtUnixMs)
    {
        if (sentAtUnixMs <= 0 ||
            !IsRuntimeUnlockNegotiationReason(trigger))
        {
            return;
        }

        var currentSessionId = currentSessionSecurityState.SessionId?.Value;
        var cleared = false;
        var clearedPeerStop = false;
        lock (accelerationGate)
        {
            if (!string.IsNullOrWhiteSpace(currentSessionId) &&
                string.Equals(accelerationUserStoppedSessionId, currentSessionId, StringComparison.Ordinal) &&
                sentAtUnixMs >= accelerationUserStoppedUtcMs)
            {
                accelerationUserStoppedSessionId = null;
                accelerationUserStoppedUtcMs = 0;
                cleared = true;
            }

            if (!string.IsNullOrWhiteSpace(currentSessionId) &&
                string.Equals(accelerationPeerUserStoppedSessionId, currentSessionId, StringComparison.Ordinal) &&
                sentAtUnixMs >= accelerationPeerUserStoppedUtcMs)
            {
                accelerationPeerUserStoppedSessionId = null;
                accelerationPeerUserStoppedUtcMs = 0;
                clearedPeerStop = true;
            }
        }

        if (cleared)
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_user_stop_cleared; trigger=peer_{SanitizeLogToken(messageType)}");
        }

        if (clearedPeerStop)
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_peer_user_stop_cleared; trigger=peer_{SanitizeLogToken(messageType)}");
        }
    }

    private static bool IsRuntimeUnlockNegotiationReason(string? reason)
        => string.Equals(SanitizeLogToken(reason), "runtime_unlock", StringComparison.Ordinal);

    private static bool IsRuntimeUnlockActivationReason(string? reason)
    {
        var normalized = SanitizeLogToken(reason);
        return string.Equals(normalized, "runtime_unlock", StringComparison.Ordinal) ||
               normalized.StartsWith("retry_runtime_unlock", StringComparison.Ordinal) ||
               normalized.StartsWith("runtime_unlock_", StringComparison.Ordinal);
    }

    private static bool IsRuntimeUnlockActivationRetryReason(string? reason)
    {
        var normalized = SanitizeLogToken(reason);
        return IsRuntimeUnlockActivationReason(normalized) ||
               string.Equals(normalized, "peer_user_stopped_tuna", StringComparison.Ordinal);
    }

    private static bool IsUserRequestedAccelerationStopReason(string? reason)
    {
        var normalized = SanitizeLogToken(reason);
        return normalized is "header_switch_off" or
            "soak_switch_off" or
            "runtime_disabled" or
            "wallet_unlinked" or
            "user_locked" or
            "user_disabled" or
            "user_stopped_tuna";
    }

    private static bool IsRemoteUserRequestedAccelerationStopReason(string? reason)
    {
        var normalized = SanitizeLogToken(reason);
        return normalized.StartsWith("remote_", StringComparison.Ordinal) &&
               IsUserRequestedAccelerationStopReason(normalized["remote_".Length..]);
    }

    private bool ShouldSuppressTunaFallbackProofAfterUserStop(string reason, string? fallbackSessionId)
    {
        if (IsUserRequestedAccelerationStopReason(reason) ||
            IsRemoteUserRequestedAccelerationStopReason(reason))
        {
            return true;
        }

        if (!IsAccelerationUserStoppedForFallbackSession(fallbackSessionId))
        {
            if (!IsAccelerationPeerUserStoppedForFallbackSession(fallbackSessionId))
            {
                return false;
            }
        }

        return reason is
            "sidecar_read_failed" or
            "sidecar_write_failed" or
            "sidecar_remote_closed" or
            "sidecar_queue_overflow" or
            "sidecar_status_timeout" or
            "sidecar_invalid_status" or
            "sidecar_status_parse_failed" or
            "sidecar_local_ipc_eof" or
            "sidecar_tuna_stream_eof" or
            "sidecar_local_write_failed" or
            "sidecar_tuna_write_failed" or
            "sidecar_listener_exited" or
            "sidecar_dialer_exited" or
            "sidecar_process_exited" or
            "sidecar_unexpected_exit" or
            "sidecar_disposed";
    }

    private bool ShouldStartFileTransferFallbackAfterUserStop(string reason, NknAccelerationLaneKind lanes)
    {
        if ((lanes & NknAccelerationLaneKind.File) != NknAccelerationLaneKind.File ||
            !HasActiveFileTransferDataSessionsForRecovery())
        {
            return false;
        }

        return IsUserRequestedAccelerationStopReason(reason) ||
               IsRemoteUserRequestedAccelerationStopReason(reason);
    }

    private bool IsAccelerationUserStoppedForFallbackSession(string? fallbackSessionId)
    {
        var sessionId = string.IsNullOrWhiteSpace(fallbackSessionId)
            ? currentSessionSecurityState.SessionId?.Value
            : fallbackSessionId.Trim();
        lock (accelerationGate)
        {
            return !string.IsNullOrWhiteSpace(sessionId) &&
                   string.Equals(accelerationUserStoppedSessionId, sessionId, StringComparison.Ordinal);
        }
    }

    private void MarkAccelerationPeerUserStoppedForCurrentSession(string? sessionId = null)
    {
        var stoppedSessionId = string.IsNullOrWhiteSpace(sessionId)
            ? currentSessionSecurityState.SessionId?.Value
            : sessionId.Trim();
        if (string.IsNullOrWhiteSpace(stoppedSessionId))
        {
            return;
        }

        lock (accelerationGate)
        {
            accelerationPeerUserStoppedSessionId = stoppedSessionId;
            accelerationPeerUserStoppedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }

    private void ClearAccelerationPeerUserStoppedForCurrentSession()
    {
        var currentSessionId = currentSessionSecurityState.SessionId?.Value;
        lock (accelerationGate)
        {
            if (string.IsNullOrWhiteSpace(currentSessionId) ||
                string.Equals(accelerationPeerUserStoppedSessionId, currentSessionId, StringComparison.Ordinal))
            {
                accelerationPeerUserStoppedSessionId = null;
                accelerationPeerUserStoppedUtcMs = 0;
            }
        }
    }

    private bool IsAccelerationPeerUserStoppedForFallbackSession(string? fallbackSessionId)
    {
        var sessionId = string.IsNullOrWhiteSpace(fallbackSessionId)
            ? currentSessionSecurityState.SessionId?.Value
            : fallbackSessionId.Trim();
        lock (accelerationGate)
        {
            return !string.IsNullOrWhiteSpace(sessionId) &&
                   string.Equals(accelerationPeerUserStoppedSessionId, sessionId, StringComparison.Ordinal);
        }
    }

    private static bool ShouldResetRemotePayerDecisionForResetReason(string? reason)
    {
        var normalized = SanitizeLogToken(reason);
        return normalized is
            "dispose" or
            "disposed" or
            "reset_session_tracking" or
            "session_security_state_not_eligible" or
            "session_not_eligible";
    }

    private long ResolvePayerDecisionIdForNegotiation(string reason)
    {
        var normalizedReason = SanitizeLogToken(reason);
        var current = Volatile.Read(ref accelerationPayerDecisionId);
        if (current <= 0 ||
            IsRuntimeUnlockNegotiationReason(normalizedReason) ||
            string.Equals(normalizedReason, "helpee_payer_preferred", StringComparison.Ordinal) ||
            string.Equals(normalizedReason, "remote_payer_intent", StringComparison.Ordinal))
        {
            return AdvancePayerDecisionEpoch(normalizedReason);
        }

        return current;
    }

    private long AdvancePayerDecisionEpoch(string reason)
    {
        var payerDecisionId = Interlocked.Increment(ref accelerationPayerDecisionId);
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_payer_decision_started; payer_decision_id={payerDecisionId}; reason={SanitizeLogToken(reason)}");
        return payerDecisionId;
    }

    private bool IsStaleLocalPayerDecision(long payerDecisionId)
    {
        if (payerDecisionId <= 0)
        {
            return false;
        }

        return payerDecisionId != Volatile.Read(ref accelerationPayerDecisionId);
    }

    private bool TryObserveRemotePayerDecision(long payerDecisionId, string messageType)
    {
        if (payerDecisionId <= 0)
        {
            return true;
        }

        lock (accelerationGate)
        {
            if (payerDecisionId < remoteAccelerationPayerDecisionId)
            {
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=tuna_remote_payer_decision_stale; message_type={SanitizeLogToken(messageType)}; payer_decision_id={payerDecisionId}; latest_payer_decision_id={remoteAccelerationPayerDecisionId}");
                return false;
            }

            if (payerDecisionId > remoteAccelerationPayerDecisionId)
            {
                remoteAccelerationPayerDecisionId = payerDecisionId;
            }
        }

        return true;
    }

    private bool IsStaleRemotePayerDecision(long payerDecisionId)
    {
        if (payerDecisionId <= 0)
        {
            return false;
        }

        lock (accelerationGate)
        {
            return payerDecisionId < remoteAccelerationPayerDecisionId;
        }
    }

    private bool ShouldAcceptDelayedHelpeeOfferDespiteStalePayerDecision(TransportAccelerationOfferPayload offer)
    {
        if (offer.PayerDecisionId <= 0 ||
            IsAccelerationNegotiatedAndHealthy() ||
            !IsHelperSessionRole(ResolveLocalSessionRole()) ||
            !IsHelpeeSessionRole(offer.SenderRole) ||
            accelerationLane is not INknTunaAccelerationSession tunaSession)
        {
            return false;
        }

        var remoteHelpeeStillWantsToListen =
            GetFreshRemoteHelpeePayerIntentState() == RemoteHelpeePayerIntentWillListen;
        var localHelperCannotCompeteAsListener =
            !tunaSession.CanOfferListener || string.IsNullOrWhiteSpace(tunaSession.LocalTunaAddress);
        if (!remoteHelpeeStillWantsToListen && !localHelperCannotCompeteAsListener)
        {
            return false;
        }

        lock (accelerationGate)
        {
            return offer.PayerDecisionId < remoteAccelerationPayerDecisionId;
        }
    }

    private bool ShouldYieldLocalPaidListenerToRemoteHelpeeIntent(TransportAccelerationPayerIntentPayload intent)
        => IsHelperSessionRole(ResolveLocalSessionRole()) &&
           IsHelpeeSessionRole(intent.SenderRole) &&
           string.Equals(SanitizeLogToken(intent.Intent), "will_listen", StringComparison.Ordinal) &&
           !IsAccelerationNegotiatedAndHealthy();

    private void YieldLocalPaidListenerToRemoteHelpee(string trigger, long remotePayerDecisionId)
    {
        if (accelerationLane is not INknTunaAccelerationSession)
        {
            return;
        }

        lock (accelerationGate)
        {
            if (!RetireRuntimeUnlockOfferForPendingAnswerLocked("payer_yield_pending_runtime_unlock_answer"))
            {
                ClearOutboundAccelerationOfferLocked();
            }
        }

        AdvancePayerDecisionEpoch("yield_to_helpee_payer");
        NotifyTransportAccelerationStateChanged("suppressed_by_peer_payer");
        ScheduleAccelerationLaneStop("payer_yield_to_helpee");
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_payer_yield; trigger={SanitizeLogToken(trigger)}; remote_payer_decision_id={remotePayerDecisionId}");
    }

    private static string FormatAccelerationLanesForLog(NknAccelerationLaneKind lanes)
    {
        var names = NknAccelerationLaneCodec.ToNames(lanes);
        return names.Length == 0 ? "(none)" : string.Join(",", names);
    }

    private async Task<bool> ShouldSuppressLocalPaidOfferForHelpeePriorityAsync(
        string localRole,
        string reason,
        CancellationToken ct)
    {
        if (!IsHelperSessionRole(localRole))
        {
            return false;
        }

        var payerIntent = GetFreshRemoteHelpeePayerIntentState();
        if (payerIntent == RemoteHelpeePayerIntentWillListen ||
            Volatile.Read(ref remoteHelpeeAccelerationOfferObserved) != 0 ||
            IsAccelerationNegotiatedAndHealthy())
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_offer_suppressed; reason=helpee_payer_priority; role=helper; trigger={SanitizeLogToken(reason)}");
            return true;
        }

        if (payerIntent == RemoteHelpeePayerIntentDialerOnly)
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_offer_delay_short_circuited; reason=helpee_payer_intent_dialer_only; role=helper; trigger={SanitizeLogToken(reason)}");
            return false;
        }

        if (Interlocked.Exchange(ref helperPaidOfferPriorityDelayConsumed, 1) != 0)
        {
            return false;
        }

        var delay = HelperPaidOfferHelpeePriorityDelayOverrideForTests ?? HelperPaidOfferHelpeePriorityDelay;
        if (delay <= TimeSpan.Zero)
        {
            return false;
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_offer_deferred; reason=helpee_payer_priority; role=helper; delay_ms={(int)delay.TotalMilliseconds}; intent_grace_ms={(int)GetHelperPaidOfferIntentGraceDelay(delay).TotalMilliseconds}; trigger={SanitizeLogToken(reason)}");
        try
        {
            var startedUtc = DateTimeOffset.UtcNow;
            var intentGraceDelay = GetHelperPaidOfferIntentGraceDelay(delay);
            while (DateTimeOffset.UtcNow - startedUtc < delay)
            {
                var remaining = delay - (DateTimeOffset.UtcNow - startedUtc);
                var step = remaining > TimeSpan.FromMilliseconds(100)
                    ? TimeSpan.FromMilliseconds(100)
                    : remaining;
                if (step > TimeSpan.Zero)
                {
                    await Task.Delay(step, ct).ConfigureAwait(false);
                }

                payerIntent = GetFreshRemoteHelpeePayerIntentState();
                if (payerIntent == RemoteHelpeePayerIntentWillListen ||
                    Volatile.Read(ref remoteHelpeeAccelerationOfferObserved) != 0 ||
                    IsAccelerationNegotiatedAndHealthy())
                {
                    LocalOperationalLog.Info(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_offer_suppressed; reason=helpee_payer_priority; role=helper; trigger={SanitizeLogToken(reason)}");
                    return true;
                }

                if (payerIntent == RemoteHelpeePayerIntentDialerOnly)
                {
                    LocalOperationalLog.Info(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_offer_delay_short_circuited; reason=helpee_payer_intent_dialer_only; role=helper; trigger={SanitizeLogToken(reason)}");
                    return false;
                }

                if (DateTimeOffset.UtcNow - startedUtc >= intentGraceDelay)
                {
                    LocalOperationalLog.Info(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_offer_delay_short_circuited; reason=helpee_payer_intent_unobserved; role=helper; waited_ms={(int)(DateTimeOffset.UtcNow - startedUtc).TotalMilliseconds}; max_delay_ms={(int)delay.TotalMilliseconds}; trigger={SanitizeLogToken(reason)}");
                    return false;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return true;
        }

        if (disposed ||
            Volatile.Read(ref remoteHelpeeAccelerationOfferObserved) != 0 ||
            IsAccelerationNegotiatedAndHealthy() ||
            !IsSessionAccelerationEligible(out _))
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_offer_suppressed; reason=helpee_payer_priority; role=helper; trigger={SanitizeLogToken(reason)}");
            return true;
        }

        return false;
    }

    private static TimeSpan GetHelperPaidOfferIntentGraceDelay(TimeSpan maxDelay)
    {
        var grace = HelperPaidOfferHelpeeIntentGraceDelayOverrideForTests ?? HelperPaidOfferHelpeeIntentGraceDelay;
        if (grace <= TimeSpan.Zero || grace >= maxDelay)
        {
            return maxDelay;
        }

        return grace;
    }

    private int GetFreshRemoteHelpeePayerIntentState()
    {
        var observedMs = Volatile.Read(ref remoteHelpeePayerIntentObservedUtcMs);
        if (observedMs <= 0)
        {
            return RemoteHelpeePayerIntentUnknown;
        }

        var ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - observedMs;
        return ageMs < 0 || ageMs > RemotePayerIntentFreshness.TotalMilliseconds
            ? RemoteHelpeePayerIntentUnknown
            : Volatile.Read(ref remoteHelpeePayerIntentState);
    }

    private void ObserveRemoteOfferForPayerPriority(
        TransportAccelerationOfferPayload offer,
        AccelerationValidationResult validation)
    {
        if (validation.IsHardReject ||
            !validation.IsValid ||
            !IsHelperSessionRole(ResolveLocalSessionRole()) ||
            !IsHelpeeSessionRole(offer.SenderRole))
        {
            return;
        }

        Interlocked.Exchange(ref remoteHelpeeAccelerationOfferObserved, 1);
    }

    private void ObserveRemotePayerIntentForPayerPriority(
        TransportAccelerationPayerIntentPayload intent,
        AccelerationValidationResult validation)
    {
        if (validation.IsHardReject ||
            !validation.IsValid ||
            !IsHelperSessionRole(ResolveLocalSessionRole()) ||
            !IsHelpeeSessionRole(intent.SenderRole))
        {
            return;
        }

        var normalizedIntent = SanitizeLogToken(intent.Intent);
        var state = normalizedIntent == "will_listen"
            ? RemoteHelpeePayerIntentWillListen
            : RemoteHelpeePayerIntentDialerOnly;
        Interlocked.Exchange(ref remoteHelpeePayerIntentState, state);
        Interlocked.Exchange(ref remoteHelpeePayerIntentObservedUtcMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private bool ShouldRejectRemoteHelperOfferForHelpeePriority(TransportAccelerationOfferPayload offer)
    {
        if (!IsHelpeeSessionRole(ResolveLocalSessionRole()) ||
            !IsHelperSessionRole(offer.SenderRole) ||
            accelerationLane is not INknTunaAccelerationSession tunaSession ||
            !tunaSession.CanOfferListener ||
            string.IsNullOrWhiteSpace(tunaSession.LocalTunaAddress))
        {
            return false;
        }

        return true;
    }

    private string ResolveLocalSessionRole()
    {
        var local = LocalPeerAddress;
        if (currentSessionSecurityState.HelpeeAddress is PeerAddress helpee &&
            AddressesLikelySamePeer(local, helpee.Value))
        {
            return "helpee";
        }

        if (currentSessionSecurityState.HelperAddress is PeerAddress helper &&
            AddressesLikelySamePeer(local, helper.Value))
        {
            return "helper";
        }

        return "unknown";
    }

    private static bool IsHelperSessionRole(string? role)
        => string.Equals(role?.Trim(), "helper", StringComparison.OrdinalIgnoreCase);

    private static bool IsHelpeeSessionRole(string? role)
        => string.Equals(role?.Trim(), "helpee", StringComparison.OrdinalIgnoreCase);

    private static bool TryDeserializeAccelerationPayload<T>(byte[] payload, out T? value)
    {
        value = default;
        try
        {
            value = JsonSerializer.Deserialize<T>(payload);
            return value is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void RejectAccelerationEnvelope(string messageType, string reason, string messageId)
    {
        NknRuntimeDiagnostics.SetLastError($"{messageType}_{reason}");
        NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{messageType}_{reason}");
        LocalOperationalLog.Warn(
            "NKN.Tuna",
            $"event=tuna_acceleration_message_rejected; message_type={messageType}; reason={reason}; msg_id={messageId}");
    }

    private static string FormatFileTransferV6TransportEpochStateForLog(V6TransportEpochState state)
        => state switch
        {
            V6TransportEpochState.EpochStarting => "epoch_starting",
            V6TransportEpochState.TargetProofPending => "target_proof_pending",
            V6TransportEpochState.FrontierRepairOnly => "frontier_repair_only",
            V6TransportEpochState.BackfillRepair => "backfill_repair",
            V6TransportEpochState.Recovered => "recovered",
            V6TransportEpochState.WaitingForTargetTransport => "waiting_for_target_transport",
            V6TransportEpochState.Terminal => "terminal",
            _ => "none",
        };

    private static string FormatFileTransferTransportHandoffKindForLog(FileTransferTransportHandoffKind kind)
        => kind switch
        {
            FileTransferTransportHandoffKind.NormalToTunaActivation => "normal_to_tuna_activation",
            FileTransferTransportHandoffKind.TunaToNormalFallback => "tuna_to_normal_fallback",
            FileTransferTransportHandoffKind.TunaRestart => "tuna_restart",
            FileTransferTransportHandoffKind.RegularNknRecovery => "regular_nkn_recovery",
            _ => "none",
        };

    private static string FormatFileTransferTransportKindForLog(FileTransferTransportKind kind)
        => kind switch
        {
            FileTransferTransportKind.RegularNkn => "regular_nkn",
            FileTransferTransportKind.Tuna => "tuna",
            _ => "unknown",
        };

    private static string SanitizeLogToken(string? value)
    {
        var safe = string.IsNullOrWhiteSpace(value) ? "(none)" : value.Trim();
        if (safe.Length > 160)
        {
            safe = safe[..160];
        }

        return safe
            .Replace(";", ",", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }
}
