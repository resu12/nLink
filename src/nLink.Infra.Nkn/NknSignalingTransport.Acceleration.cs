using System.Diagnostics;
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
    private const int AccelerationEarlyDropMaxRetryAttempts = 1;
    private static readonly TimeSpan AccelerationOfferLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AccelerationOfferAnswerTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AccelerationOfferReplayDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AccelerationNegotiationRetryBaseDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HelperPaidOfferHelpeePriorityDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HelperPaidOfferHelpeeIntentGraceDelay = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan AccelerationListenerReadyRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan AccelerationControlDirectSendWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AccelerationControlBulkBypassWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AccelerationAnswerAckTimeout = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan AccelerationAnswerReplayDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AccelerationAnswerAckReplayDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FileTransferTunaActivationPauseMax = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan RemotePayerIntentFreshness = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TunaFallbackProofLogWindow = TimeSpan.FromMinutes(1);
    private const long TunaFallbackProofLogEveryFrames = 5000;
    private const int AccelerationOfferReplayAttempts = 12;
    private const int AccelerationAnswerReplayAttempts = 3;
    private const int AccelerationAnswerAckReplayAttempts = 3;
    private const int RemoteHelpeePayerIntentUnknown = 0;
    private const int RemoteHelpeePayerIntentWillListen = 1;
    private const int RemoteHelpeePayerIntentDialerOnly = 2;
    [ThreadStatic]
    private static bool handlingTunaAcceleratedInboundMessage;
    internal static TimeSpan? AccelerationOfferAnswerTimeoutOverrideForTests;
    internal static TimeSpan? AccelerationOfferReplayDelayOverrideForTests;
    internal static TimeSpan? AccelerationControlDirectSendWaitOverrideForTests;
    internal static TimeSpan? AccelerationControlBulkBypassWaitOverrideForTests;
    internal static TimeSpan? HelperPaidOfferHelpeePriorityDelayOverrideForTests;
    internal static TimeSpan? HelperPaidOfferHelpeeIntentGraceDelayOverrideForTests;
    private readonly object accelerationGate = new();
    private string? outboundAccelerationOfferNonce;
    private string? outboundAccelerationOfferTrigger;
    private long outboundAccelerationOfferGeneration;
    private string? accelerationSessionId;
    private NknAccelerationLaneKind accelerationNegotiatedLanes;
    private int accelerationNegotiationScheduled;
    private int accelerationNegotiationRetryAttempts;
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
    private string? pendingAccelerationAnswerAckSessionId;
    private string? pendingAccelerationAnswerAckNonce;
    private NknAccelerationLaneKind pendingAccelerationAnswerAckLanes;
    private long pendingAccelerationAnswerAckPayerDecisionId;
    private long pendingAccelerationAnswerAckGeneration;
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

        if (client is not RealNknClientAdapter realClient)
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=filetransfer_v6_bridge_receive_recovery_request_unsupported; session_id={SanitizeLogToken(sessionId ?? "none")}; transfer_id={SanitizeLogToken(transferId)}; direction={direction}; reason={reason}");
            return;
        }

        var accepted = realClient.RequestFileTransferReceiveStallRecovery(reason);
        LocalOperationalLog.Warn(
            "NKN.Tuna",
            $"event=filetransfer_v6_bridge_receive_recovery_requested; session_id={SanitizeLogToken(sessionId ?? "none")}; transfer_id={SanitizeLogToken(transferId)}; direction={direction}; reason={reason}; accepted={(accepted ? 1 : 0)}");
        if (!accepted)
        {
            return;
        }

        if (ShouldUseFileTransferV6EpochForRegularNknRecovery(sessionId))
        {
            MarkFileTransferFallbackNknProofPending(
                reason,
                sessionId,
                NknAccelerationLaneKind.File);
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

    public void ObserveRegularV4ControlFeedbackPressure(FileTransferRegularV4ControlFeedbackPressure pressure)
    {
        var sessionId = string.IsNullOrWhiteSpace(pressure.SessionId)
            ? currentSessionSecurityState.SessionId?.Value
            : pressure.SessionId.Trim();
        var transferId = string.IsNullOrWhiteSpace(pressure.TransferId) ? "(none)" : pressure.TransferId.Trim();
        var reason = string.IsNullOrWhiteSpace(pressure.Reason)
            ? "regular_v4_control_feedback_pressure"
            : SanitizeLogToken(pressure.Reason);

        if (client is not RealNknClientAdapter realClient)
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=filetransfer_regular_v4_control_feedback_pressure_unsupported; session_id={SanitizeLogToken(sessionId ?? "none")}; transfer_id={SanitizeLogToken(transferId)}; reason={reason}");
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
            return;
        }

        ConsumePostTunaFileFallbackRoute(
            notification.SessionId,
            notification.TransferId,
            "post_tuna_fallback_v6_transfer_completed");
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
        TunaFallbackProofState? stateToLog = null;
        lock (accelerationGate)
        {
            if (tunaFallbackProofState is { } existing &&
                string.Equals(existing.SessionId, normalizedSessionId, StringComparison.Ordinal))
            {
                if (IsFileTransferFallbackFinalForLanes(existing, lanes))
                {
                    LocalOperationalLog.Info(
                        "NKN.Tuna",
                        $"event=filetransfer_v6_fallback_start_suppressed_final; session_id={SanitizeLogToken(normalizedSessionId)}; reason={normalizedReason}; existing_reason={existing.Reason}; file_state={FormatTunaFallbackLaneState(existing.FileState)}; file_v6_epoch_state={FormatTunaFallbackFileV6EpochState(existing)}");
                    return false;
                }

                if (!existing.AccelerationUsedAfterFallback)
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

    private void PauseFileTransferDataSessionsForTunaActivationNegotiation(
        string reason,
        string? sessionId,
        string trigger)
    {
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId)
            ? currentSessionSecurityState.SessionId?.Value
            : sessionId.Trim();
        if (string.IsNullOrWhiteSpace(normalizedSessionId))
        {
            return;
        }

        long generation;
        lock (accelerationGate)
        {
            if (string.Equals(fileTransferTunaActivationPauseSessionId, normalizedSessionId, StringComparison.Ordinal))
            {
                return;
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
    {
        sessionId = currentSessionSecurityState.SessionId?.Value;
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
        if (!IsFileTransferFallbackNknProofPending() ||
            !TryDeserializeFileTransferDataFrameFromWire(payload, out var frame) ||
            frame is null ||
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
        if (TryMapFileTransferFallbackNknProofKind(frame, out var proofKind))
        {
            _ = CompleteFileTransferFallbackNknProofIfPending(proofKind, sessionId);
        }
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
        NknAccelerationLaneKind lanes)
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
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=filetransfer_fallback_nkn_proof_pending; session_id={SanitizeLogToken(normalizedSessionId ?? "none")}; reason={normalizedReason}; lanes={FormatAccelerationLanesForLog(lanes)}");
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
                    fileTransferFallbackProofPending = false;
                    fileTransferFallbackProofReason = "none";
                    fileTransferFallbackProofSessionId = null;
                    fileTransferFallbackProofLanes = NknAccelerationLaneKind.None;
                    fileTransferFallbackProofGeneration++;
                    fileTransferFallbackProofProbeScheduled = false;
                    fileTransferFallbackBulkProofObserved = false;
                    fileTransferFallbackControlProofObserved = false;
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
                completedPendingProof = true;
                fileTransferFallbackProofPending = false;
                fileTransferFallbackProofReason = "none";
                fileTransferFallbackProofSessionId = null;
                fileTransferFallbackProofLanes = NknAccelerationLaneKind.None;
                fileTransferFallbackProofGeneration++;
                fileTransferFallbackProofProbeScheduled = false;
                fileTransferFallbackBulkProofObserved = false;
                fileTransferFallbackControlProofObserved = false;
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
        => false;

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
            ResetAccelerationNegotiation(NormalizeAccelerationSidecarResetReason(e.Reason));
            if (shouldNotifyRemoteDown)
            {
                ScheduleAccelerationDownNotification(downSessionId, downLanes, reason);
            }

            if (shouldRetryEarlyDrop)
            {
                ScheduleAccelerationEarlyDropRetry(reason, diagnostics);
            }

            return;
        }

        NotifyTransportAccelerationStateChanged(e.Reason);
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
            return;
        }

        var payerDecisionId = ResolvePayerDecisionIdForNegotiation(reason);
        NotifyTransportAccelerationStateChanged($"negotiation_scheduled_{reason}");
        _ = Task.Run(
            async () =>
            {
                try
                {
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

        if (IsFileTransferUsingRegularNknFallbackForCurrentSession())
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=filetransfer_tuna_activation_negotiation_pause_skipped; session_id={SanitizeLogToken(sessionId)}; reason=active_post_tuna_fallback; trigger={SanitizeLogToken(reason)}");
        }
        else
        {
            PauseFileTransferDataSessionsForTunaActivationNegotiation(
                "activation_negotiation_pending",
                sessionId,
                reason);
            pausedForActivationNegotiation = true;
        }

        try
        {
            var payerIntentQueued = await SendAccelerationPayerIntentAsync(
                    remoteEndpoint,
                    sessionId,
                    envelopeCode,
                    localRole,
                    preflightLanes,
                    tunaSession.CanOfferListener,
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

        if (!tunaSession.CanOfferListener)
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
        if (!await tunaSession.EnsureListenerSidecarConnectedAsync(remoteEndpoint, ct).ConfigureAwait(false) ||
            string.IsNullOrWhiteSpace(tunaSession.LocalTunaAddress))
        {
            NotifyTransportAccelerationStateChanged("listener_sidecar_unavailable");
            ResumeActivationPauseIfNeeded("tuna_activation_failed_regular_v4_resumed", "listener_sidecar_unavailable");
            ScheduleAccelerationNegotiationRetry("listener_sidecar_unavailable");
            return;
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
        lock (accelerationGate)
        {
            outboundAccelerationOfferNonce = nonce;
            outboundAccelerationOfferTrigger = SanitizeLogToken(reason);
            outboundAccelerationOfferPayerDecisionId = payerDecisionId;
            offerGeneration = ++outboundAccelerationOfferGeneration;
        }

        var queued = await SendAccelerationControlEnvelopeWithBulkBypassAsync(
                remoteEndpoint,
                envelope,
                "offer",
                ct)
            .ConfigureAwait(false);
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_offer_{(queued ? "queued" : "rejected")}; reason={SanitizeLogToken(reason)}; session_id={sessionId}; lanes={string.Join(",", offer.SupportedLanes)}; payer_decision_id={payerDecisionId}");
        if (!queued)
        {
            NotifyTransportAccelerationStateChanged("offer_queue_rejected");
            ResumeActivationPauseIfNeeded("offer_queue_rejected", "offer_queue_rejected");
            ScheduleAccelerationNegotiationRetry("offer_queue_rejected");
            return;
        }

        NotifyTransportAccelerationStateChanged("waiting_for_answer");
        ScheduleAccelerationOfferReplay(remoteEndpoint, envelope, sessionId, nonce, payerDecisionId, offerGeneration);
        ScheduleAccelerationOfferAnswerTimeout(nonce);
    }

    private void ScheduleAccelerationOfferReplay(
        string target,
        Envelope envelope,
        string sessionId,
        string nonce,
        long payerDecisionId,
        long generation)
    {
        _ = Task.Run(
            async () =>
            {
                var delay = AccelerationOfferReplayDelayOverrideForTests ?? AccelerationOfferReplayDelay;
                for (var attempt = 1; attempt <= AccelerationOfferReplayAttempts; attempt++)
                {
                    await Task.Delay(delay, CancellationToken.None).ConfigureAwait(false);
                    if (!IsOutboundAccelerationOfferPending(nonce, payerDecisionId, generation))
                    {
                        return;
                    }

                    var queued = await SendAccelerationControlEnvelopeWithBulkBypassAsync(
                            target,
                            envelope,
                            "offer_replay",
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    LocalOperationalLog.Info(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_offer_replay_{(queued ? "sent" : "rejected")}; attempt={attempt}; session_id={SanitizeLogToken(sessionId)}; payer_decision_id={payerDecisionId}; generation={generation}");
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
            $"event=tuna_acceleration_payer_intent_{(queued ? "queued" : "rejected")}; intent={intent}; role={SanitizeLogToken(localRole)}; trigger={SanitizeLogToken(trigger)}; lanes={FormatAccelerationLanesForLog(lanes)}; payer_decision_id={payerDecisionId}");
        return queued;
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
            PauseFileTransferDataSessionsForTunaActivationNegotiation(
                "peer_offer_dialer_starting",
                offer.SessionId,
                offer.Trigger);
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
        pendingAccelerationAnswerAckLanes = NknAccelerationLaneKind.None;
        pendingAccelerationAnswerAckPayerDecisionId = 0;
        pendingAccelerationAnswerAckGeneration++;
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

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(AccelerationAnswerAckTimeout, CancellationToken.None).ConfigureAwait(false);
                    if (disposed ||
                        !IsPendingAccelerationAnswerAck(sessionId, nonce, payerDecisionId, generation))
                    {
                        return;
                    }

                    lock (accelerationGate)
                    {
                        if (!IsPendingAccelerationAnswerAck(sessionId, nonce, payerDecisionId, generation))
                        {
                            return;
                        }

                        ClearPendingAccelerationAnswerAckLocked();
                    }

                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_answer_ack_timeout; session_id={SanitizeLogToken(sessionId)}; timeout_ms={(long)AccelerationAnswerAckTimeout.TotalMilliseconds}; payer_decision_id={payerDecisionId}; generation={generation}");
                    ResumeFileTransferDataSessionsAfterTunaActivationNegotiation(
                        "answer_ack_timeout",
                        sessionId,
                        "answer_ack_timeout");
                    NotifyTransportAccelerationStateChanged("answer_ack_timeout");
                    ScheduleAccelerationLaneStop("answer_ack_timeout");
                    ScheduleAccelerationNegotiationRetry("answer_ack_timeout");
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
        var queued = await SendAccelerationControlEnvelopeWithBulkBypassAsync(
                remoteEndpoint,
                envelope,
                "answer",
                ct)
            .ConfigureAwait(false);
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_answer_{(queued ? "sent" : "rejected")}; accepted={(accepted ? 1 : 0)}; reason={SanitizeLogToken(rejectReason)}; lanes={string.Join(",", answer.SupportedLanes)}; payer_decision_id={answer.PayerDecisionId}");
        if (accepted && queued && pendingAnswerAckGeneration > 0)
        {
            ScheduleAccelerationAnswerReplay(
                remoteEndpoint,
                envelope,
                answer.SessionId,
                answer.Nonce,
                answer.PayerDecisionId,
                pendingAnswerAckGeneration);
        }
    }

    private void ScheduleAccelerationAnswerReplay(
        string target,
        Envelope envelope,
        string sessionId,
        string nonce,
        long payerDecisionId,
        long generation)
    {
        if (generation <= 0)
        {
            return;
        }

        _ = Task.Run(
            async () =>
            {
                for (var attempt = 1; attempt <= AccelerationAnswerReplayAttempts; attempt++)
                {
                    try
                    {
                        await Task.Delay(AccelerationAnswerReplayDelay, CancellationToken.None).ConfigureAwait(false);
                        if (disposed ||
                            !IsPendingAccelerationAnswerAck(sessionId, nonce, payerDecisionId, generation))
                        {
                            return;
                        }

                        var queued = await SendAccelerationControlEnvelopeWithBulkBypassAsync(
                                target,
                                envelope,
                                "answer_replay",
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        LocalOperationalLog.Info(
                            "NKN.Tuna",
                            $"event=tuna_acceleration_answer_replay_{(queued ? "sent" : "rejected")}; attempt={attempt}; session_id={SanitizeLogToken(sessionId)}; payer_decision_id={payerDecisionId}; generation={generation}");
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
            $"event=tuna_acceleration_answer_ack_{(queued ? "sent" : "rejected")}; session_id={SanitizeLogToken(answer.SessionId)}; lanes={FormatAccelerationLanesForLog(lanes)}; payer_decision_id={answer.PayerDecisionId}");
        if (queued)
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
                            $"event=tuna_acceleration_answer_ack_replay_{(queued ? "sent" : "rejected")}; attempt={attempt}; session_id={SanitizeLogToken(sessionId)}; payer_decision_id={payerDecisionId}");
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

    private async Task<bool> SendAccelerationControlEnvelopeWithBulkBypassAsync(
        string target,
        Envelope envelope,
        string purpose,
        CancellationToken ct)
    {
        var bytes = EnvelopeCodec.Serialize(envelope);
        var controlTask = QueueControlEnvelopeAsync(target, envelope, ControlOutboundLane.High, ct);
        ObserveAccelerationControlSendTask(controlTask, purpose, "control_queue");

        var priorityControlTask = SendAccelerationControlPriorityCopyAsync(target, envelope, bytes, purpose, ct);
        ObserveAccelerationControlSendTask(priorityControlTask, purpose, "control_priority");

        Task<bool>? bulkTask = null;
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
            bulkTask = SendAccelerationControlBulkCopyAsync(remoteBulkEndpoint, envelope, bytes, purpose, ct);
            ObserveAccelerationControlSendTask(bulkTask, purpose, "control_to_bulk_endpoint");
        }

        if (IsAccelerationControlQueueAccepted(controlTask, ct, out var queueRejected))
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_control_queue_accepted; purpose={SanitizeLogToken(purpose)}; message_type={MapAccelerationControlMessageType(envelope.Type)}; completed={(controlTask.IsCompletedSuccessfully ? 1 : 0)}");
            return true;
        }

        if (queueRejected)
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_acceleration_control_queue_rejected; purpose={SanitizeLogToken(purpose)}; message_type={MapAccelerationControlMessageType(envelope.Type)}");
        }

        var attempts = new List<Task<bool>> { priorityControlTask, controlTask };
        if (bulkTask is not null)
        {
            attempts.Add(bulkTask);
        }

        return await WaitForFirstSuccessfulAccelerationControlSendAsync(attempts, purpose).ConfigureAwait(false);
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

    private static async Task<bool> WaitForFirstSuccessfulAccelerationControlSendAsync(
        List<Task<bool>> attempts,
        string purpose)
    {
        if (attempts.Count == 0)
        {
            return false;
        }

        var waitTimeout = ResolveAccelerationControlBulkBypassWait();
        var remaining = new List<Task<bool>>(attempts);
        var timeoutTask = Task.Delay(waitTimeout);
        while (remaining.Count > 0)
        {
            var completed = await Task.WhenAny(remaining.Cast<Task>().Append(timeoutTask)).ConfigureAwait(false);
            if (ReferenceEquals(completed, timeoutTask))
            {
                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_control_send_wait_timeout; purpose={SanitizeLogToken(purpose)}; wait_ms={(long)waitTimeout.TotalMilliseconds}; remaining={remaining.Count}");
                return false;
            }

            var completedAttempt = (Task<bool>)completed;
            remaining.Remove(completedAttempt);
            try
            {
                if (await completedAttempt.ConfigureAwait(false))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_control_send_attempt_failed; purpose={SanitizeLogToken(purpose)}; error={ex.GetType().Name}");
            }
        }

        return false;
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

    private async Task<bool> SendAccelerationControlBulkCopyAsync(
        string destination,
        Envelope envelope,
        byte[] bytes,
        string purpose,
        CancellationToken ct)
    {
        try
        {
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
                return true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_acceleration_control_bulk_bypass_priority_failed; purpose={SanitizeLogToken(purpose)}; message_type={MapAccelerationControlMessageType(envelope.Type)}; error={ex.GetType().Name}");
        }

        try
        {
            await SendBulkEnvelopeAsync(destination, envelope, bytes, ct, allowAcceleration: false).ConfigureAwait(false);
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_control_bulk_bypass_sent; purpose={SanitizeLogToken(purpose)}; message_type={MapAccelerationControlMessageType(envelope.Type)}; lane=bulk_queue_fallback");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_acceleration_control_bulk_bypass_failed; purpose={SanitizeLogToken(purpose)}; message_type={MapAccelerationControlMessageType(envelope.Type)}; error={ex.GetType().Name}");
            return false;
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

        string? expectedNonce;
        string? expectedTrigger;
        long expectedPayerDecisionId;
        bool matchedRetiredOffer;
        var answerSessionId = string.IsNullOrWhiteSpace(answer.SessionId) ? string.Empty : answer.SessionId.Trim();
        var answerNonce = string.IsNullOrWhiteSpace(answer.Nonce) ? string.Empty : answer.Nonce.Trim();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (accelerationGate)
        {
            matchedRetiredOffer = false;
            if (!string.IsNullOrWhiteSpace(outboundAccelerationOfferNonce) &&
                string.Equals(outboundAccelerationOfferNonce, answerNonce, StringComparison.Ordinal))
            {
                expectedNonce = outboundAccelerationOfferNonce;
                expectedTrigger = outboundAccelerationOfferTrigger;
                expectedPayerDecisionId = outboundAccelerationOfferPayerDecisionId;
            }
            else if (!string.IsNullOrWhiteSpace(retiredAccelerationOfferNonce) &&
                     string.Equals(retiredAccelerationOfferNonce, answerNonce, StringComparison.Ordinal) &&
                     string.Equals(retiredAccelerationOfferSessionId, answerSessionId, StringComparison.Ordinal) &&
                     nowMs < retiredAccelerationOfferExpiresUtcMs)
            {
                expectedNonce = retiredAccelerationOfferNonce;
                expectedTrigger = retiredAccelerationOfferTrigger;
                expectedPayerDecisionId = retiredAccelerationOfferPayerDecisionId;
                matchedRetiredOffer = true;
            }
            else
            {
                expectedNonce = null;
                expectedTrigger = null;
                expectedPayerDecisionId = 0;
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
            if (string.Equals(SanitizeLogToken(effectiveRejectReason), "helpee_payer_preferred", StringComparison.Ordinal))
            {
                AdvancePayerDecisionEpoch("yield_to_helpee_payer");
                NotifyTransportAccelerationStateChanged("suppressed_by_peer_payer");
                ScheduleAccelerationLaneStop("payer_yield_to_helpee");
                return;
            }

            var retryPeerStopAfterUnlock = ShouldRetryPeerUserStoppedAfterRuntimeUnlock(effectiveRejectReason, expectedTrigger);
            if (ShouldRetryAccelerationNegotiation(effectiveRejectReason) || retryPeerStopAfterUnlock)
            {
                ScheduleAccelerationNegotiationRetry(retryPeerStopAfterUnlock ? "peer_user_stopped_tuna" : effectiveRejectReason!);
            }

            return;
        }

        lock (accelerationGate)
        {
            if (matchedRetiredOffer)
            {
                ClearRetiredOutboundAccelerationOfferLocked();
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_retired_offer_answer_accepted; session_id={SanitizeLogToken(answer.SessionId)}; payer_decision_id={answer.PayerDecisionId}");
            }
            else
            {
                ClearOutboundAccelerationOfferLocked();
            }

            ClearPendingAccelerationAnswerAckLocked();
            accelerationSessionId = answer.SessionId.Trim();
            accelerationNegotiatedLanes = validation.AcceptedLanes;
        }

        _ = Task.Run(
            () => SendAccelerationAnswerAckAsync(answer, validation.AcceptedLanes, CancellationToken.None),
            CancellationToken.None);
        Interlocked.Exchange(ref accelerationNegotiationRetryAttempts, 0);
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_negotiated; session_id={answer.SessionId}; lanes={string.Join(",", answer.SupportedLanes)}; payer_decision_id={answer.PayerDecisionId}");
        RequestFileTransferTunaActivationHandoff(answer.SessionId, validation.AcceptedLanes, "tuna_activation_negotiated");
        NotifyTransportAccelerationStateChanged(GetActiveAccelerationStatusReason());
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

    private void ScheduleAccelerationNegotiationRetry(string reason)
    {
        if (disposed ||
            accelerationLane is not INknTunaAccelerationSession ||
            IsAccelerationNegotiatedAndHealthy() ||
            !IsSessionAccelerationEligible(out _))
        {
            return;
        }

        if (TryGetUnresolvedFileTransferV6TransportEpochForCurrentSession(out var pendingEpoch) &&
            !ShouldAllowAccelerationRetryDespiteUnresolvedV6Epoch(pendingEpoch, reason, "preflight"))
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_acceleration_retry_blocked_v6_epoch_unresolved; reason={SanitizeLogToken(reason)}; session_id={SanitizeLogToken(pendingEpoch.SessionId)}; transfer_id={SanitizeLogToken(pendingEpoch.TransferId)}; direction={pendingEpoch.Direction.ToString().ToLowerInvariant()}; transport_epoch={pendingEpoch.TransportEpoch}; state={FormatFileTransferV6TransportEpochStateForLog(pendingEpoch.State)}; handoff_kind={FormatFileTransferTransportHandoffKindForLog(pendingEpoch.HandoffKind)}; target_transport={FormatFileTransferTransportKindForLog(pendingEpoch.TargetTransport)}");
            return;
        }

        if (TryGetFileTransferFallbackControlProofPendingSnapshot(out var pendingSessionId, out var pendingReason, out var pendingLanes))
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_acceleration_retry_blocked_fallback_control_unproven; reason={SanitizeLogToken(reason)}; fallback_reason={SanitizeLogToken(pendingReason)}; session_id={SanitizeLogToken(pendingSessionId ?? "none")}; lanes={FormatAccelerationLanesForLog(pendingLanes)}");
            return;
        }

        var attempt = Interlocked.Increment(ref accelerationNegotiationRetryAttempts);
        if (attempt > AccelerationNegotiationMaxRetryAttempts)
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_acceleration_retry_exhausted; reason={SanitizeLogToken(reason)}; attempts={attempt - 1}");
            if (TryGetActiveFileTransferTunaActivationPauseForCurrentSession(out var pausedSessionId))
            {
                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=filetransfer_tuna_activation_retry_exhausted_resuming_regular_nkn; reason={SanitizeLogToken(reason)}; session_id={SanitizeLogToken(pausedSessionId ?? "none")}; attempts={attempt - 1}");
                ResumeFileTransferDataSessionsAfterTunaActivationNegotiation(
                    $"retry_exhausted_{SanitizeLogToken(reason)}",
                    pausedSessionId,
                    "retry_exhausted");
                return;
            }

            ResumeFileTransferDataSessionsAfterTunaActivationNegotiation(
                $"retry_exhausted_{SanitizeLogToken(reason)}",
                currentSessionSecurityState.SessionId?.Value,
                "retry_exhausted");
            return;
        }

        var useListenerReadyFastRetry = ShouldUseListenerReadyFastRetry(reason);
        var delay = useListenerReadyFastRetry
            ? AccelerationListenerReadyRetryDelay
            : TimeSpan.FromMilliseconds(
                AccelerationNegotiationRetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_retry_scheduled; reason={SanitizeLogToken(reason)}; attempt={attempt}; delay_ms={(int)delay.TotalMilliseconds}; listener_ready_reuse={(useListenerReadyFastRetry ? 1 : 0)}");
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(delay, CancellationToken.None).ConfigureAwait(false);
                    if (disposed ||
                        IsAccelerationNegotiatedAndHealthy() ||
                        HasPendingOutboundAccelerationOffer() ||
                        HasPendingAccelerationAnswerAck() ||
                        !IsSessionAccelerationEligible(out _))
                    {
                        return;
                    }

                    if (TryGetUnresolvedFileTransferV6TransportEpochForCurrentSession(out var delayedPendingEpoch) &&
                        !ShouldAllowAccelerationRetryDespiteUnresolvedV6Epoch(delayedPendingEpoch, reason, "delayed"))
                    {
                        LocalOperationalLog.Warn(
                            "NKN.Tuna",
                            $"event=tuna_acceleration_retry_skipped_v6_epoch_unresolved; reason={SanitizeLogToken(reason)}; session_id={SanitizeLogToken(delayedPendingEpoch.SessionId)}; transfer_id={SanitizeLogToken(delayedPendingEpoch.TransferId)}; direction={delayedPendingEpoch.Direction.ToString().ToLowerInvariant()}; transport_epoch={delayedPendingEpoch.TransportEpoch}; state={FormatFileTransferV6TransportEpochStateForLog(delayedPendingEpoch.State)}; handoff_kind={FormatFileTransferTransportHandoffKindForLog(delayedPendingEpoch.HandoffKind)}; target_transport={FormatFileTransferTransportKindForLog(delayedPendingEpoch.TargetTransport)}");
                        return;
                    }

                    if (TryGetFileTransferFallbackControlProofPendingSnapshot(out var delayedPendingSessionId, out var delayedPendingReason, out var delayedPendingLanes))
                    {
                        LocalOperationalLog.Warn(
                            "NKN.Tuna",
                            $"event=tuna_acceleration_retry_skipped_fallback_control_unproven; reason={SanitizeLogToken(reason)}; fallback_reason={SanitizeLogToken(delayedPendingReason)}; session_id={SanitizeLogToken(delayedPendingSessionId ?? "none")}; lanes={FormatAccelerationLanesForLog(delayedPendingLanes)}");
                        return;
                    }

                    LocalOperationalLog.Info(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_retry_fired; reason={SanitizeLogToken(reason)}; listener_ready_reuse={(useListenerReadyFastRetry ? 1 : 0)}");
                    ScheduleAccelerationNegotiationIfEligible(
                        string.Equals(SanitizeLogToken(reason), "peer_user_stopped_tuna", StringComparison.Ordinal)
                            ? "runtime_unlock"
                            : $"retry_{reason}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_retry_failed; reason={SanitizeLogToken(reason)}; error={ex.GetType().Name}");
                }
            },
            CancellationToken.None);
    }

    private static bool IsRegularNknRecoveryEpoch(FileTransferV6TransportEpochSnapshot snapshot)
        => snapshot.TargetTransport == FileTransferTransportKind.RegularNkn &&
           snapshot.HandoffKind == FileTransferTransportHandoffKind.RegularNknRecovery;

    private bool ShouldAllowAccelerationRetryDespiteUnresolvedV6Epoch(
        FileTransferV6TransportEpochSnapshot pendingEpoch,
        string reason,
        string stage)
    {
        if (!IsRegularNknRecoveryEpoch(pendingEpoch))
        {
            return false;
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_retry_allowed_regular_nkn_recovery_unresolved; reason={SanitizeLogToken(reason)}; stage={SanitizeLogToken(stage)}; session_id={SanitizeLogToken(pendingEpoch.SessionId)}; transfer_id={SanitizeLogToken(pendingEpoch.TransferId)}; direction={pendingEpoch.Direction.ToString().ToLowerInvariant()}; transport_epoch={pendingEpoch.TransportEpoch}; state={FormatFileTransferV6TransportEpochStateForLog(pendingEpoch.State)}; handoff_kind={FormatFileTransferTransportHandoffKindForLog(pendingEpoch.HandoffKind)}; target_transport={FormatFileTransferTransportKindForLog(pendingEpoch.TargetTransport)}");
        return true;
    }

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
            ScheduleAccelerationNegotiationRetry($"preflight_{normalizedReason}");
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

    private void ScheduleAccelerationOfferAnswerTimeout(string nonce)
    {
        var timeout = AccelerationOfferAnswerTimeoutOverrideForTests ?? AccelerationOfferAnswerTimeout;
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
                        !IsSessionAccelerationEligible(out _))
                    {
                        return;
                    }

                    lock (accelerationGate)
                    {
                        if (!string.Equals(outboundAccelerationOfferNonce, nonce, StringComparison.Ordinal))
                        {
                            return;
                        }

                        outboundAccelerationOfferNonce = null;
                        outboundAccelerationOfferTrigger = null;
                        outboundAccelerationOfferPayerDecisionId = 0;
                    }

                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_offer_answer_timeout; timeout_ms={(int)timeout.TotalMilliseconds}");
                    NotifyTransportAccelerationStateChanged("offer_answer_timeout");
                    ResumeFileTransferDataSessionsAfterTunaActivationFailure(
                        "offer_answer_timeout",
                        currentSessionSecurityState.SessionId?.Value,
                        "offer_answer_timeout");
                    ScheduleAccelerationNegotiationRetry("offer_answer_timeout");
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

    private bool ShouldUseListenerReadyFastRetry(string? reason)
    {
        var normalized = SanitizeLogToken(reason);
        if (normalized is not ("sidecar_unavailable" or "offer_answer_timeout" or "offer_queue_rejected" or "peer_user_stopped_tuna" or "preflight_listener_unavailable" or "early_drop_remote_closed"))
        {
            return false;
        }

        return accelerationLane is INknTunaAccelerationSession tunaSession &&
               tunaSession.CanOfferListener &&
               tunaSession.IsAvailable &&
               !string.IsNullOrWhiteSpace(tunaSession.LocalTunaAddress);
    }

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
        CancellationToken ct)
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

        if (ShouldSuppressAcceleratedFileTransferBulkDuringRegularNknFallback(lane))
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
            IsAccelerationNegotiatedAndHealthy() ||
            IsAccelerationUserStoppedForCurrentSession() ||
            !IsSessionAccelerationEligible(out _))
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
        lock (accelerationGate)
        {
            fallbackSessionId = accelerationSessionId;
            fallbackLanes = accelerationNegotiatedLanes;
            ClearOutboundAccelerationOfferLocked();
            ClearRetiredOutboundAccelerationOfferLocked();
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
        Interlocked.Exchange(ref accelerationNegotiationRetryAttempts, 0);
        Interlocked.Exchange(ref helperPaidOfferPriorityDelayConsumed, 0);
        Interlocked.Exchange(ref remoteHelpeeAccelerationOfferObserved, 0);
        Interlocked.Exchange(ref remoteHelpeePayerIntentState, RemoteHelpeePayerIntentUnknown);
        Interlocked.Exchange(ref remoteHelpeePayerIntentObservedUtcMs, 0);
        AdvancePayerDecisionEpoch($"reset_{normalizedReason}");
        LocalOperationalLog.Info("NKN.Tuna", $"event=tuna_acceleration_reset; reason={normalizedReason}; fallback_proof_suppressed={(suppressFallbackProof ? 1 : 0)}");
        NotifyTransportAccelerationStateChanged(normalizedReason);
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
            outboundAccelerationOfferNonce = null;
            outboundAccelerationOfferTrigger = null;
            outboundAccelerationOfferPayerDecisionId = 0;
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
