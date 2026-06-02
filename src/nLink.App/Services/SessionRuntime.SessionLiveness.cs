using System;
using System.Threading;
using System.Threading.Tasks;
using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using NLink.Core.Resources;
using NLink.Infra.Nkn;

namespace NLink.App.Services;

public sealed partial class SessionRuntime
{
    private static readonly TimeSpan SessionLivenessActiveFileTransferRecoveryDeferral = TimeSpan.FromSeconds(10);
    private const int SessionLivenessActiveFileTransferRecoveryDeferralLimit = 1;
    private static readonly TimeSpan SessionLivenessActiveFileTransferBridgeRecoveryDeferral = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan SessionLivenessActiveFileTransferLongBridgeRecoveryDeferral = TimeSpan.FromSeconds(35);
    private const int SessionLivenessActiveFileTransferBridgeRecoveryDeferralLimit = 4;
    private static readonly TimeSpan SessionLivenessActiveFileTransferMaxDeferrableSilence = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan SessionLivenessRuntimeUnlockStartupDeferral = TimeSpan.FromSeconds(60);
    private const int SessionLivenessRuntimeUnlockStartupDeferralLimit = 1;

    private readonly object sessionLivenessGate = new();
    private CancellationTokenSource? sessionLivenessCts;
    private long sessionLivenessGeneration;
    private long sessionLivenessSequence;
    private DateTimeOffset sessionLivenessLastPeerProofUtc;
    private DateTimeOffset sessionLivenessLastAckUtc;
    private DateTimeOffset sessionLivenessFileTransferRecoveryDeferralUntilUtc;
    private int sessionLivenessHeartbeatInFlight;
    private int sessionLivenessConsecutiveSendFailures;
    private int sessionLivenessFileTransferRecoveryDeferralCount;
    private int sessionLivenessFileTransferBridgeRecoveryDeferralCount;
    private int sessionLivenessRuntimeUnlockStartupDeferralCount;
    private long sessionLivenessRecoveryContractDeferralGeneration;
    private string? sessionLivenessFileTransferRecoveryDeferralKey;
    private bool sessionLivenessSuspectLogged;

    private void StartSessionLivenessWatchdog(string reason)
    {
        if (!watchdogOptions.Enabled ||
            watchdogOptions.SessionLivenessHeartbeatInterval <= TimeSpan.Zero ||
            watchdogOptions.SessionLivenessSuspectTimeout <= TimeSpan.Zero ||
            watchdogOptions.SessionLivenessTimeout <= TimeSpan.Zero ||
            state != SessionRuntimeState.Connected ||
            transportState != TransportState.Connected ||
            transport is not ISessionLivenessSignalingTransport livenessTransport)
        {
            return;
        }

        var sessionIdSnapshot = GetApprovedSessionIdForLiveness();
        if (string.IsNullOrWhiteSpace(sessionIdSnapshot))
        {
            return;
        }

        CancelSessionLivenessWatchdog("restart:" + reason);

        var cts = new CancellationTokenSource();
        var generation = Interlocked.Increment(ref sessionLivenessGeneration);
        var now = nowProvider();
        lock (sessionLivenessGate)
        {
            sessionLivenessCts = cts;
            sessionLivenessLastPeerProofUtc = now;
            sessionLivenessLastAckUtc = now;
            sessionLivenessSuspectLogged = false;
            sessionLivenessConsecutiveSendFailures = 0;
            sessionLivenessFileTransferRecoveryDeferralCount = 0;
            sessionLivenessFileTransferBridgeRecoveryDeferralCount = 0;
            sessionLivenessRuntimeUnlockStartupDeferralCount = 0;
            sessionLivenessRecoveryContractDeferralGeneration = 0;
            sessionLivenessFileTransferRecoveryDeferralKey = null;
            sessionLivenessFileTransferRecoveryDeferralUntilUtc = DateTimeOffset.MinValue;
            sessionLivenessHeartbeatInFlight = 0;
        }

        LocalOperationalLog.Info(
            "Session",
            $"event=session_liveness_watchdog_started; session_id={sessionIdSnapshot}; generation={generation}; heartbeat_interval_ms={(long)watchdogOptions.SessionLivenessHeartbeatInterval.TotalMilliseconds}; suspect_timeout_ms={(long)watchdogOptions.SessionLivenessSuspectTimeout.TotalMilliseconds}; terminal_timeout_ms={(long)watchdogOptions.SessionLivenessTimeout.TotalMilliseconds}; reason={reason}; role={role}; run_id={GetRunIdForLog()}; scenario={GetScenarioForLog()}");

        ActiveRuntimeCounters.IncWatchdogs();
        RunCountedBackgroundTask(
            async () =>
            {
                try
                {
                    await RunSessionLivenessWatchdogAsync(
                            livenessTransport,
                            sessionIdSnapshot,
                            generation,
                            cts.Token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        cts.Dispose();
                    }
                    catch
                    {
                        // Best-effort cleanup only.
                    }

                    ActiveRuntimeCounters.DecWatchdogs();
                }
            },
            countAsTransportTask: false);
    }

    private async Task RunSessionLivenessWatchdogAsync(
        ISessionLivenessSignalingTransport livenessTransport,
        string sessionIdSnapshot,
        long generation,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await watchdogDelayAsync(watchdogOptions.SessionLivenessHeartbeatInterval, ct).ConfigureAwait(false);
                if (await CheckSessionLivenessAsync(livenessTransport, sessionIdSnapshot, generation, ct).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Error(
                "Session",
                $"event=session_liveness_watchdog_internal_error; session_id={sessionIdSnapshot}; generation={generation}; error={ex.GetType().Name}; role={role}; state={state}; transport_state={transportState}; run_id={GetRunIdForLog()}; scenario={GetScenarioForLog()}");
        }
    }

    private async Task<bool> CheckSessionLivenessAsync(
        ISessionLivenessSignalingTransport livenessTransport,
        string sessionIdSnapshot,
        long generation,
        CancellationToken ct)
    {
        DateTimeOffset lastProof;
        DateTimeOffset fileTransferRecoveryDeferralUntil;
        string? fileTransferRecoveryDeferralKey;
        bool suspectAlreadyLogged;
        lock (sessionLivenessGate)
        {
            if (generation != sessionLivenessGeneration ||
                sessionLivenessCts is null ||
                state != SessionRuntimeState.Connected ||
                transportState != TransportState.Connected ||
                transport is not ISessionLivenessSignalingTransport ||
                !string.Equals(GetApprovedSessionIdForLiveness(), sessionIdSnapshot, StringComparison.Ordinal))
            {
                return true;
            }

            lastProof = sessionLivenessLastPeerProofUtc;
            fileTransferRecoveryDeferralUntil = sessionLivenessFileTransferRecoveryDeferralUntilUtc;
            fileTransferRecoveryDeferralKey = sessionLivenessFileTransferRecoveryDeferralKey;
            suspectAlreadyLogged = sessionLivenessSuspectLogged;
        }

        var now = nowProvider();
        var silence = now - lastProof;
        if (silence >= watchdogOptions.SessionLivenessTimeout)
        {
            if (fileTransferRecoveryDeferralUntil > now &&
                TryGetValidFileTransferRecoveryLivenessSnapshot(
                    sessionIdSnapshot,
                    now,
                    out var currentRecoverySnapshot) &&
                string.Equals(
                    fileTransferRecoveryDeferralKey,
                    CreateFileTransferRecoveryLivenessDeferralKey(currentRecoverySnapshot),
                    StringComparison.Ordinal))
            {
                LocalOperationalLog.Info(
                    "Session",
                    $"event=session_liveness_timeout_deferred_waiting_for_current_filetransfer_recovery; session_id={sessionIdSnapshot}; transfer_id={currentRecoverySnapshot.TransferId}; route={currentRecoverySnapshot.RouteToken}; protocol_version={currentRecoverySnapshot.ProtocolVersion}; live_route_epoch={currentRecoverySnapshot.LiveRouteEpoch}; leg_generation={currentRecoverySnapshot.TransferLegGeneration}; bridge_recovery_generation={currentRecoverySnapshot.BridgeRecoveryGeneration}; transport_epoch={currentRecoverySnapshot.TransportEpoch}; generation={generation}; silence_ms={(long)silence.TotalMilliseconds}; remaining_ms={(long)(fileTransferRecoveryDeferralUntil - now).TotalMilliseconds}; role={role}; run_id={GetRunIdForLog()}; scenario={GetScenarioForLog()}");
                await TrySendSessionLivenessHeartbeatAsync(
                        livenessTransport,
                        sessionIdSnapshot,
                        generation,
                        "timeout_waiting_current_filetransfer_recovery",
                        ct)
                    .ConfigureAwait(false);
                return false;
            }

            var fileTransferDeferralCapReached = IsSessionLivenessFileTransferDeferralCapReached(
                sessionIdSnapshot,
                generation,
                silence,
                "timeout_check",
                "pending_deferral");
            if (fileTransferRecoveryDeferralUntil > now &&
                !fileTransferDeferralCapReached)
            {
                LocalOperationalLog.Info(
                    "Session",
                    $"event=session_liveness_timeout_deferred_waiting_for_filetransfer_recovery; session_id={sessionIdSnapshot}; generation={generation}; silence_ms={(long)silence.TotalMilliseconds}; remaining_ms={(long)(fileTransferRecoveryDeferralUntil - now).TotalMilliseconds}; role={role}; run_id={GetRunIdForLog()}; scenario={GetScenarioForLog()}");
                await TrySendSessionLivenessHeartbeatAsync(
                        livenessTransport,
                        sessionIdSnapshot,
                        generation,
                        "timeout_deferred_filetransfer_recovery",
                        ct)
                    .ConfigureAwait(false);
                return false;
            }
            else if (fileTransferRecoveryDeferralUntil > now)
            {
                ClearExpiredSessionLivenessFileTransferDeferral(generation, sessionIdSnapshot, "deferral_cap_reached");
            }

            if (TryDeferSessionLivenessTimeoutForFileTransferRecoveryLivenessState(sessionIdSnapshot, generation, silence))
            {
                await TrySendSessionLivenessHeartbeatAsync(
                        livenessTransport,
                        sessionIdSnapshot,
                        generation,
                        "timeout_current_filetransfer_recovery",
                        ct)
                    .ConfigureAwait(false);
                return false;
            }

            if (TryDeferSessionLivenessTimeoutForRuntimeUnlockStartup(sessionIdSnapshot, generation, silence))
            {
                await TrySendSessionLivenessHeartbeatAsync(
                        livenessTransport,
                        sessionIdSnapshot,
                        generation,
                        "timeout_runtime_unlock_startup",
                        ct)
                    .ConfigureAwait(false);
                return false;
            }

            if (TryDeferSessionLivenessTimeoutForFileTransferRecovery(sessionIdSnapshot, generation, silence))
            {
                await TrySendSessionLivenessHeartbeatAsync(
                        livenessTransport,
                        sessionIdSnapshot,
                        generation,
                        "timeout_filetransfer_recovery",
                        ct)
                    .ConfigureAwait(false);
                return false;
            }

            if (TryDeferSessionLivenessTimeoutForRecoveryContract(sessionIdSnapshot, generation, silence))
            {
                await TrySendSessionLivenessHeartbeatAsync(
                        livenessTransport,
                        sessionIdSnapshot,
                        generation,
                        "timeout_session_recovery_contract",
                        ct)
                    .ConfigureAwait(false);
                return false;
            }

            await HandleSessionLivenessTimeoutAsync(sessionIdSnapshot, generation, silence).ConfigureAwait(false);
            return true;
        }

        var urgentHeartbeat = false;
        if (silence >= watchdogOptions.SessionLivenessSuspectTimeout && !suspectAlreadyLogged)
        {
            lock (sessionLivenessGate)
            {
                if (generation == sessionLivenessGeneration && !sessionLivenessSuspectLogged)
                {
                    sessionLivenessSuspectLogged = true;
                    urgentHeartbeat = true;
                }
            }

            LocalOperationalLog.Warn(
                "Session",
                $"event=session_liveness_suspect; session_id={sessionIdSnapshot}; generation={generation}; silence_ms={(long)silence.TotalMilliseconds}; suspect_timeout_ms={(long)watchdogOptions.SessionLivenessSuspectTimeout.TotalMilliseconds}; role={role}; run_id={GetRunIdForLog()}; scenario={GetScenarioForLog()}");
        }

        await TrySendSessionLivenessHeartbeatAsync(
                livenessTransport,
                sessionIdSnapshot,
                generation,
                urgentHeartbeat ? "suspect_urgent" : "periodic",
                ct)
            .ConfigureAwait(false);
        return false;
    }

    private bool TryDeferSessionLivenessTimeoutForFileTransferRecovery(
        string sessionIdSnapshot,
        long generation,
        TimeSpan silence)
    {
        if (IsSessionLivenessFileTransferDeferralCapReached(
                sessionIdSnapshot,
                generation,
                silence,
                "filetransfer_recovery_request",
                "session_liveness_timeout_pending"))
        {
            return false;
        }

        if (transport is not IFileTransferReceiveRecoveryController recoveryController ||
            !TryCreateSessionLivenessReceiveRecoveryRequest(
                sessionIdSnapshot,
                "session_liveness_timeout_pending",
                out var recoveryRequest))
        {
            return false;
        }

        int nextDeferralCount;
        lock (sessionLivenessGate)
        {
            if (generation != sessionLivenessGeneration ||
                sessionLivenessCts is null ||
                state != SessionRuntimeState.Connected ||
                transportState != TransportState.Connected ||
                !string.Equals(GetApprovedSessionIdForLiveness(), sessionIdSnapshot, StringComparison.Ordinal) ||
                sessionLivenessFileTransferRecoveryDeferralCount >= SessionLivenessActiveFileTransferRecoveryDeferralLimit)
            {
                return false;
            }

            nextDeferralCount = sessionLivenessFileTransferRecoveryDeferralCount + 1;
        }

        try
        {
            recoveryController.RequestFileTransferReceiveRecovery(recoveryRequest);
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn(
                "Session",
                $"event=session_liveness_timeout_filetransfer_recovery_request_failed; session_id={sessionIdSnapshot}; transfer_id={recoveryRequest.TransferId}; direction={recoveryRequest.Direction.ToString().ToLowerInvariant()}; error={ex.GetType().Name}; role={role}; run_id={GetRunIdForLog()}; scenario={GetScenarioForLog()}");
            return false;
        }

        lock (sessionLivenessGate)
        {
            if (generation != sessionLivenessGeneration ||
                sessionLivenessCts is null ||
                state != SessionRuntimeState.Connected ||
                transportState != TransportState.Connected ||
                !string.Equals(GetApprovedSessionIdForLiveness(), sessionIdSnapshot, StringComparison.Ordinal))
            {
                return false;
            }

            sessionLivenessFileTransferRecoveryDeferralCount = nextDeferralCount;
            sessionLivenessFileTransferRecoveryDeferralUntilUtc =
                nowProvider().Add(SessionLivenessActiveFileTransferRecoveryDeferral);
        }

        LocalOperationalLog.Warn(
            "Session",
            $"event=session_liveness_timeout_deferred_for_filetransfer_recovery; session_id={sessionIdSnapshot}; transfer_id={recoveryRequest.TransferId}; direction={recoveryRequest.Direction.ToString().ToLowerInvariant()}; generation={generation}; silence_ms={(long)silence.TotalMilliseconds}; deferral_count={nextDeferralCount}; deferral_limit={SessionLivenessActiveFileTransferRecoveryDeferralLimit}; deferral_ms={(long)SessionLivenessActiveFileTransferRecoveryDeferral.TotalMilliseconds}; role={role}; run_id={GetRunIdForLog()}; scenario={GetScenarioForLog()}");
        return true;
    }

    private bool TryDeferSessionLivenessTimeoutForFileTransferRecoveryLivenessState(
        string sessionIdSnapshot,
        long generation,
        TimeSpan silence)
    {
        if (transport is not IFileTransferRecoveryLivenessState recoveryState ||
            !recoveryState.TryGetActiveFileTransferRecoveryLivenessSnapshot(sessionIdSnapshot, out var snapshot))
        {
            return false;
        }

        var now = nowProvider();
        var metadataValid = IsValidFileTransferRecoveryLivenessSnapshot(sessionIdSnapshot, snapshot);
        if (!metadataValid ||
            !IsActiveFileTransferRecoveryLivenessSnapshot(snapshot, now))
        {
            LocalOperationalLog.Warn(
                "Session",
                $"event=session_liveness_timeout_filetransfer_recovery_state_not_deferred; session_id={sessionIdSnapshot}; transfer_id={snapshot.TransferId}; route={snapshot.RouteToken}; protocol_version={snapshot.ProtocolVersion}; live_route_epoch={snapshot.LiveRouteEpoch}; leg_generation={snapshot.TransferLegGeneration}; bridge_recovery_generation={snapshot.BridgeRecoveryGeneration}; transport_epoch={snapshot.TransportEpoch}; state={snapshot.State.ToString().ToLowerInvariant()}; metadata_valid={(metadataValid ? 1 : 0)}; terminal_recommended={(snapshot.TerminalRecommended ? 1 : 0)}; authority_completed={(snapshot.AuthorityCompleted ? 1 : 0)}; receive_proof_observed={(snapshot.ReceiveProofObserved ? 1 : 0)}; recovery_exhausted={(snapshot.RecoveryExhausted ? 1 : 0)}; liveness_deferral_deadline_utc_ms={snapshot.LivenessDeferralDeadlineUtc.ToUnixTimeMilliseconds()}; silence_ms={(long)silence.TotalMilliseconds}; role={role}; run_id={GetRunIdForLog()}; scenario={GetScenarioForLog()}");
            return false;
        }

        var deferralKey = CreateFileTransferRecoveryLivenessDeferralKey(snapshot);
        lock (sessionLivenessGate)
        {
            if (generation != sessionLivenessGeneration ||
                sessionLivenessCts is null ||
                state != SessionRuntimeState.Connected ||
                transportState != TransportState.Connected ||
                !string.Equals(GetApprovedSessionIdForLiveness(), sessionIdSnapshot, StringComparison.Ordinal) ||
                string.Equals(sessionLivenessFileTransferRecoveryDeferralKey, deferralKey, StringComparison.Ordinal))
            {
                return false;
            }

            sessionLivenessFileTransferRecoveryDeferralKey = deferralKey;
            sessionLivenessFileTransferRecoveryDeferralUntilUtc = snapshot.LivenessDeferralDeadlineUtc;
        }

        LocalOperationalLog.Warn(
            "Session",
            $"event=session_liveness_timeout_deferred_for_current_filetransfer_recovery; session_id={sessionIdSnapshot}; transfer_id={snapshot.TransferId}; route={snapshot.RouteToken}; protocol_version={snapshot.ProtocolVersion}; live_route_epoch={snapshot.LiveRouteEpoch}; leg_generation={snapshot.TransferLegGeneration}; bridge_recovery_generation={snapshot.BridgeRecoveryGeneration}; transport_epoch={snapshot.TransportEpoch}; checkpoint_request_id={SanitizeSessionLivenessReason(snapshot.CheckpointRequestId ?? "(none)")}; authority_reason={SanitizeSessionLivenessReason(snapshot.AuthorityReason)}; state={snapshot.State.ToString().ToLowerInvariant()}; bridge_recovery_requested={(snapshot.BridgeRecoveryRequested ? 1 : 0)}; bridge_recovery_started={(snapshot.BridgeRecoveryStarted ? 1 : 0)}; bridge_recovery_completed={(snapshot.BridgeRecoveryCompleted ? 1 : 0)}; silence_ms={(long)silence.TotalMilliseconds}; liveness_deferral_deadline_utc_ms={snapshot.LivenessDeferralDeadlineUtc.ToUnixTimeMilliseconds()}; role={role}; run_id={GetRunIdForLog()}; scenario={GetScenarioForLog()}");
        return true;
    }

    private bool TryGetValidFileTransferRecoveryLivenessSnapshot(
        string sessionIdSnapshot,
        DateTimeOffset now,
        out FileTransferRecoveryLivenessSnapshot snapshot)
    {
        snapshot = default!;
        if (transport is not IFileTransferRecoveryLivenessState recoveryState ||
            !recoveryState.TryGetActiveFileTransferRecoveryLivenessSnapshot(sessionIdSnapshot, out var candidate) ||
            !IsValidFileTransferRecoveryLivenessSnapshot(sessionIdSnapshot, candidate) ||
            !IsActiveFileTransferRecoveryLivenessSnapshot(candidate, now))
        {
            return false;
        }

        snapshot = candidate;
        return true;
    }

    private static bool IsValidFileTransferRecoveryLivenessSnapshot(
        string sessionIdSnapshot,
        FileTransferRecoveryLivenessSnapshot snapshot)
        => string.Equals(snapshot.SessionId, sessionIdSnapshot, StringComparison.Ordinal) &&
           string.Equals(snapshot.RouteToken, "post_tuna_fallback_v6", StringComparison.Ordinal) &&
           snapshot.ProtocolVersion == FileTransferProtocol.ProtocolVersionV6 &&
           snapshot.TransferLegGeneration > 0 &&
           snapshot.LiveRouteEpoch > 0;

    private static bool IsActiveFileTransferRecoveryLivenessSnapshot(
        FileTransferRecoveryLivenessSnapshot snapshot,
        DateTimeOffset now)
        => !snapshot.TerminalRecommended &&
           !snapshot.AuthorityCompleted &&
           !snapshot.ReceiveProofObserved &&
           !snapshot.RecoveryExhausted &&
           now <= snapshot.LivenessDeferralDeadlineUtc;

    private static string CreateFileTransferRecoveryLivenessDeferralKey(FileTransferRecoveryLivenessSnapshot snapshot)
        => $"{snapshot.SessionId}:{snapshot.TransferId}:{snapshot.TransferLegGeneration}:{snapshot.BridgeRecoveryGeneration}:{snapshot.TransportEpoch}";

    private bool TryDeferSessionLivenessTimeoutForRuntimeUnlockStartup(
        string sessionIdSnapshot,
        long generation,
        TimeSpan silence)
    {
        if (IsSessionLivenessFileTransferDeferralCapReached(
                sessionIdSnapshot,
                generation,
                silence,
                "runtime_unlock_startup",
                transportAccelerationStatusReason))
        {
            return false;
        }

        if (!TryCreateSessionLivenessReceiveRecoveryRequest(
                sessionIdSnapshot,
                "session_liveness_runtime_unlock_startup",
                out var recoveryRequest))
        {
            return false;
        }

        string statusReason;
        bool accelerationActive;
        int nextDeferralCount;
        lock (sessionLivenessGate)
        {
            if (generation != sessionLivenessGeneration ||
                sessionLivenessCts is null ||
                state != SessionRuntimeState.Connected ||
                transportState != TransportState.Connected ||
                !string.Equals(GetApprovedSessionIdForLiveness(), sessionIdSnapshot, StringComparison.Ordinal) ||
                sessionLivenessRuntimeUnlockStartupDeferralCount >= SessionLivenessRuntimeUnlockStartupDeferralLimit)
            {
                return false;
            }

            statusReason = transportAccelerationStatusReason;
            accelerationActive = transportAccelerationActive;
            if (accelerationActive ||
                !IsRuntimeUnlockActivationStartupStatus(statusReason))
            {
                return false;
            }

            nextDeferralCount = sessionLivenessRuntimeUnlockStartupDeferralCount + 1;
            sessionLivenessRuntimeUnlockStartupDeferralCount = nextDeferralCount;
            sessionLivenessFileTransferRecoveryDeferralUntilUtc =
                nowProvider().Add(SessionLivenessRuntimeUnlockStartupDeferral);
        }

        LocalOperationalLog.Warn(
            "Session",
            $"event=session_liveness_timeout_deferred_for_tuna_runtime_unlock_startup; session_id={sessionIdSnapshot}; transfer_id={recoveryRequest.TransferId}; direction={recoveryRequest.Direction.ToString().ToLowerInvariant()}; generation={generation}; silence_ms={(long)silence.TotalMilliseconds}; status_reason={SanitizeSessionLivenessReason(statusReason)}; deferral_count={nextDeferralCount}; deferral_limit={SessionLivenessRuntimeUnlockStartupDeferralLimit}; deferral_ms={(long)SessionLivenessRuntimeUnlockStartupDeferral.TotalMilliseconds}; role={role}; run_id={GetRunIdForLog()}; scenario={GetScenarioForLog()}");
        return true;
    }

    private void ObserveSessionLivenessBridgeRecoveryEvent(BridgeLifecycleEvent e)
    {
        if (e.Kind is not (BridgeLifecycleEventKind.ReceiveStallRecoveryStarted or
            BridgeLifecycleEventKind.ReceiveStallRecoveryCompleted or
            BridgeLifecycleEventKind.ReceiveStallRecoveryDeferred))
        {
            return;
        }

        var sessionIdSnapshot = GetApprovedSessionIdForLiveness();
        if (string.IsNullOrWhiteSpace(sessionIdSnapshot) ||
            !IsFileTransferBridgeRecoveryReason(e.ExitReasonText) ||
            !TryCreateSessionLivenessReceiveRecoveryRequest(
                sessionIdSnapshot,
                "session_liveness_bridge_recovery_observed",
                out var recoveryRequest))
        {
            return;
        }

        var bridgeReason = e.ExitReasonText;
        var bridgeDeferral = GetSessionLivenessBridgeRecoveryDeferral(e.Kind, bridgeReason);
        var forceDeferralOverLimit = ShouldForceSessionLivenessBridgeRecoveryDeferralOverLimit(e.Kind, bridgeReason);
        DateTimeOffset now;
        DateTimeOffset lastPeerProof;
        int nextDeferralCount;
        bool overLimit;
        lock (sessionLivenessGate)
        {
            if (sessionLivenessCts is null ||
                state != SessionRuntimeState.Connected ||
                transportState != TransportState.Connected ||
                !string.Equals(GetApprovedSessionIdForLiveness(), sessionIdSnapshot, StringComparison.Ordinal))
            {
                return;
            }

            overLimit = sessionLivenessFileTransferBridgeRecoveryDeferralCount >= SessionLivenessActiveFileTransferBridgeRecoveryDeferralLimit;
            if (overLimit && !forceDeferralOverLimit)
            {
                return;
            }

            now = nowProvider();
            lastPeerProof = sessionLivenessLastPeerProofUtc;
            if (IsSessionLivenessFileTransferDeferralCapReached(
                    sessionIdSnapshot,
                    sessionLivenessGeneration,
                    now - lastPeerProof,
                    "bridge_recovery_event",
                    bridgeReason))
            {
                return;
            }

            nextDeferralCount = overLimit
                ? sessionLivenessFileTransferBridgeRecoveryDeferralCount
                : sessionLivenessFileTransferBridgeRecoveryDeferralCount + 1;
            sessionLivenessFileTransferBridgeRecoveryDeferralCount = nextDeferralCount;
            var nextDeferralUntil = now.Add(bridgeDeferral);
            if (sessionLivenessFileTransferRecoveryDeferralUntilUtc < nextDeferralUntil)
            {
                sessionLivenessFileTransferRecoveryDeferralUntilUtc = nextDeferralUntil;
            }
        }

        LocalOperationalLog.Info(
            "Session",
            $"event=session_liveness_timeout_deferred_for_bridge_filetransfer_recovery; session_id={sessionIdSnapshot}; transfer_id={recoveryRequest.TransferId}; direction={recoveryRequest.Direction.ToString().ToLowerInvariant()}; bridge_event={e.Kind.ToString().ToLowerInvariant()}; bridge_reason={SanitizeSessionLivenessReason(e.ExitReasonText)}; deferral_count={nextDeferralCount}; deferral_limit={SessionLivenessActiveFileTransferBridgeRecoveryDeferralLimit}; deferral_over_limit={(overLimit ? 1 : 0)}; deferral_ms={(long)bridgeDeferral.TotalMilliseconds}; role={role}; run_id={GetRunIdForLog()}; scenario={GetScenarioForLog()}");
    }

    private bool IsSessionLivenessFileTransferDeferralCapReached(
        string sessionIdSnapshot,
        long generation,
        TimeSpan silence,
        string source,
        string? reason)
    {
        if (silence < SessionLivenessActiveFileTransferMaxDeferrableSilence)
        {
            return false;
        }

        LocalOperationalLog.Warn(
            "Session",
            $"event=session_liveness_filetransfer_recovery_deferral_cap_reached; session_id={sessionIdSnapshot}; generation={generation}; silence_ms={(long)silence.TotalMilliseconds}; cap_ms={(long)SessionLivenessActiveFileTransferMaxDeferrableSilence.TotalMilliseconds}; source={SanitizeSessionLivenessReason(source)}; reason={SanitizeSessionLivenessReason(reason)}; role={role}; run_id={GetRunIdForLog()}; scenario={GetScenarioForLog()}");
        return true;
    }

    private void ClearExpiredSessionLivenessFileTransferDeferral(
        long generation,
        string sessionIdSnapshot,
        string reason)
    {
        lock (sessionLivenessGate)
        {
            if (generation != sessionLivenessGeneration ||
                sessionLivenessCts is null ||
                !string.Equals(GetApprovedSessionIdForLiveness(), sessionIdSnapshot, StringComparison.Ordinal))
            {
                return;
            }

            sessionLivenessFileTransferRecoveryDeferralUntilUtc = DateTimeOffset.MinValue;
            sessionLivenessFileTransferRecoveryDeferralKey = null;
        }

        LocalOperationalLog.Warn(
            "Session",
            $"event=session_liveness_filetransfer_recovery_deferral_cleared; session_id={sessionIdSnapshot}; generation={generation}; reason={SanitizeSessionLivenessReason(reason)}; role={role}; run_id={GetRunIdForLog()}; scenario={GetScenarioForLog()}");
    }

    private static TimeSpan GetSessionLivenessBridgeRecoveryDeferral(
        BridgeLifecycleEventKind kind,
        string? reason)
    {
        if (IsLongRunningSessionLivenessBridgeRecovery(kind, reason))
        {
            return SessionLivenessActiveFileTransferLongBridgeRecoveryDeferral;
        }

        return SessionLivenessActiveFileTransferBridgeRecoveryDeferral;
    }

    private static bool ShouldForceSessionLivenessBridgeRecoveryDeferralOverLimit(
        BridgeLifecycleEventKind kind,
        string? reason)
    {
        return kind == BridgeLifecycleEventKind.ReceiveStallRecoveryStarted ||
               IsLongRunningSessionLivenessBridgeRecovery(kind, reason);
    }

    private static bool IsLongRunningSessionLivenessBridgeRecovery(
        BridgeLifecycleEventKind kind,
        string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        return reason.Contains("tuna_activation_offer_send_timeout", StringComparison.OrdinalIgnoreCase) ||
               reason.Contains("runtime_unlock_recovery", StringComparison.OrdinalIgnoreCase) ||
               reason.Contains("recovery_already_in_progress", StringComparison.OrdinalIgnoreCase) ||
               reason.Contains("active_filetransfer_unproven_cooldown", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRuntimeUnlockActivationStartupStatus(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        var normalized = reason.Trim();
        return normalized.Contains("runtime_unlock", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("renegotiating_after_user_unlock", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("checking_payer_priority", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("selected_payer_starting_listener", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("listener_starting", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("listener_ready", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("dialer_starting", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("dialer_ready", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("waiting_for_answer", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("activation_offer_not_observed", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("offer_queue_rejected", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFileTransferBridgeRecoveryReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        var normalized = reason.Trim();
        return normalized.Contains("filetransfer", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("file_transfer", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("active_filetransfer", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("regular_v4", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("post_tuna_fallback", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("tuna_activation_offer", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("bulk_receive_stalled", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("all_channels_zero_receive", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("session_liveness_timeout_pending", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("sender_request_feedback_stalled", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeSessionLivenessReason(string? reason)
        => string.IsNullOrWhiteSpace(reason)
            ? "(none)"
            : reason.Replace(';', '_').Replace('\r', '_').Replace('\n', '_').Trim();

    private bool TryCreateSessionLivenessReceiveRecoveryRequest(
        string sessionIdSnapshot,
        string reason,
        out FileTransferReceiveRecoveryRequest request)
    {
        var snapshot = latestFileTransferSnapshot;
        if (TryCreateSessionLivenessReceiveRecoveryRequest(snapshot.Outbound, sessionIdSnapshot, reason, out request) ||
            TryCreateSessionLivenessReceiveRecoveryRequest(snapshot.Inbound, sessionIdSnapshot, reason, out request))
        {
            return true;
        }

        snapshot = fileTransferService.Snapshot;
        return TryCreateSessionLivenessReceiveRecoveryRequest(snapshot.Outbound, sessionIdSnapshot, reason, out request) ||
               TryCreateSessionLivenessReceiveRecoveryRequest(snapshot.Inbound, sessionIdSnapshot, reason, out request);
    }

    private static bool TryCreateSessionLivenessReceiveRecoveryRequest(
        FileTransferTransferSnapshot? transfer,
        string sessionIdSnapshot,
        string reason,
        out FileTransferReceiveRecoveryRequest request)
    {
        request = null!;
        if (transfer is null ||
            transfer.IsTerminal ||
            string.IsNullOrWhiteSpace(transfer.TransferId) ||
            !string.Equals(transfer.SessionId, sessionIdSnapshot, StringComparison.Ordinal))
        {
            return false;
        }

        request = new FileTransferReceiveRecoveryRequest(
            sessionIdSnapshot,
            transfer.TransferId,
            transfer.Direction,
            reason);
        return true;
    }

    private async Task TrySendSessionLivenessHeartbeatAsync(
        ISessionLivenessSignalingTransport livenessTransport,
        string sessionIdSnapshot,
        long generation,
        string trigger,
        CancellationToken ct)
    {
        if (Interlocked.Exchange(ref sessionLivenessHeartbeatInFlight, 1) != 0)
        {
            LocalOperationalLog.Info(
                "Session",
                $"event=session_liveness_heartbeat_skipped; reason=heartbeat_in_flight; session_id={sessionIdSnapshot}; generation={generation}; trigger={trigger}");
            return;
        }

        try
        {
            var sequence = Interlocked.Increment(ref sessionLivenessSequence);
            var heartbeat = new SessionHeartbeatMessage(
                sessionIdSnapshot,
                generation,
                sequence,
                nowProvider().ToUnixTimeMilliseconds(),
                role.ToString().ToLowerInvariant());
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sendCts.CancelAfter(watchdogOptions.SessionLivenessHeartbeatInterval);
            await livenessTransport.SendSessionHeartbeatAsync(heartbeat, sendCts.Token).ConfigureAwait(false);
            lock (sessionLivenessGate)
            {
                if (generation == sessionLivenessGeneration)
                {
                    sessionLivenessLastAckUtc = nowProvider();
                    sessionLivenessConsecutiveSendFailures = 0;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failures = Interlocked.Increment(ref sessionLivenessConsecutiveSendFailures);
            LocalOperationalLog.Warn(
                "Session",
                $"event=session_liveness_heartbeat_send_failed; session_id={sessionIdSnapshot}; generation={generation}; trigger={trigger}; consecutive_failures={failures}; error={ex.GetType().Name}; role={role}; run_id={GetRunIdForLog()}; scenario={GetScenarioForLog()}");
        }
        finally
        {
            Interlocked.Exchange(ref sessionLivenessHeartbeatInFlight, 0);
        }
    }

    private bool TryDeferSessionLivenessTimeoutForRecoveryContract(
        string sessionIdSnapshot,
        long generation,
        TimeSpan silence)
    {
        if (transport is not ISessionRecoveryStateContract recoveryContract ||
            !recoveryContract.TryGetActiveSessionRecoveryContract(sessionIdSnapshot, out var snapshot) ||
            snapshot.Kind != SessionRecoveryContractKind.RuntimeUnlockActivation)
        {
            return false;
        }

        var now = nowProvider();
        var recoveryActionPending = snapshot.RetryRequired ||
            snapshot.RetryAuthorityPending ||
            snapshot.ObservedSendPending;
        if (!recoveryActionPending ||
            snapshot.State is SessionRecoveryContractState.Completed or SessionRecoveryContractState.Failed ||
            now > snapshot.LivenessDeferralDeadlineUtc)
        {
            LocalOperationalLog.Warn(
                "Session",
                $"event=session_liveness_timeout_session_recovery_contract_not_deferred; session_id={sessionIdSnapshot}; contract_generation={snapshot.ContractGeneration}; state={snapshot.State.ToString().ToLowerInvariant()}; retry_required={(snapshot.RetryRequired ? 1 : 0)}; retry_authority_pending={(snapshot.RetryAuthorityPending ? 1 : 0)}; observed_send_pending={(snapshot.ObservedSendPending ? 1 : 0)}; retry_dispatched={(snapshot.RetryDispatched ? 1 : 0)}; retry_observed={(snapshot.RetryObserved ? 1 : 0)}; authority_failure_reason={SanitizeSessionLivenessReason(snapshot.AuthorityFailureReason ?? "(none)")}; observed_send_deadline_utc_ms={snapshot.ObservedSendDeadlineUtc.ToUnixTimeMilliseconds()}; liveness_deferral_deadline_utc_ms={snapshot.LivenessDeferralDeadlineUtc.ToUnixTimeMilliseconds()}; silence_ms={(long)silence.TotalMilliseconds}; role={role}; run_id={GetRunIdForLog()}; scenario={GetScenarioForLog()}");
            return false;
        }

        lock (sessionLivenessGate)
        {
            if (generation != sessionLivenessGeneration ||
                sessionLivenessCts is null ||
                state != SessionRuntimeState.Connected ||
                transportState != TransportState.Connected ||
                !string.Equals(GetApprovedSessionIdForLiveness(), sessionIdSnapshot, StringComparison.Ordinal) ||
                sessionLivenessRecoveryContractDeferralGeneration == snapshot.ContractGeneration)
            {
                return false;
            }

            sessionLivenessRecoveryContractDeferralGeneration = snapshot.ContractGeneration;
        }

        LocalOperationalLog.Warn(
            "Session",
            $"event=session_liveness_timeout_deferred_for_session_recovery_contract; session_id={sessionIdSnapshot}; transfer_id={snapshot.TransferId ?? "(none)"}; contract_generation={snapshot.ContractGeneration}; offer_generation={snapshot.OfferGeneration}; kind=runtime_unlock_activation; state={snapshot.State.ToString().ToLowerInvariant()}; retry_reason={SanitizeSessionLivenessReason(snapshot.RetryReason)}; recovery_reason={SanitizeSessionLivenessReason(snapshot.RecoveryReason)}; recovery_pending={(snapshot.RecoveryPending ? 1 : 0)}; recovery_settled={(snapshot.RecoverySettled ? 1 : 0)}; retry_dispatching={(snapshot.RetryDispatching ? 1 : 0)}; retry_dispatched={(snapshot.RetryDispatched ? 1 : 0)}; retry_observed={(snapshot.RetryObserved ? 1 : 0)}; queued_behind_active_negotiation={(snapshot.QueuedBehindActiveNegotiation ? 1 : 0)}; retry_authority_pending={(snapshot.RetryAuthorityPending ? 1 : 0)}; retry_authority_granted={(snapshot.RetryAuthorityGranted ? 1 : 0)}; observed_send_pending={(snapshot.ObservedSendPending ? 1 : 0)}; authority_attempt={snapshot.AuthorityAttempt}; authorized_observed_lane={SanitizeSessionLivenessReason(snapshot.AuthorizedObservedLane ?? "(none)")}; authority_failure_reason={SanitizeSessionLivenessReason(snapshot.AuthorityFailureReason ?? "(none)")}; observed_send_deadline_utc_ms={snapshot.ObservedSendDeadlineUtc.ToUnixTimeMilliseconds()}; silence_ms={(long)silence.TotalMilliseconds}; liveness_deferral_deadline_utc_ms={snapshot.LivenessDeferralDeadlineUtc.ToUnixTimeMilliseconds()}; role={role}; run_id={GetRunIdForLog()}; scenario={GetScenarioForLog()}");
        return true;
    }

    private async Task HandleSessionLivenessTimeoutAsync(
        string sessionIdSnapshot,
        long generation,
        TimeSpan silence)
    {
        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed ||
                resetInProgress ||
                remoteSessionEndHandling ||
                lastDisconnectWasRemoteEnd ||
                generation != sessionLivenessGeneration ||
                state != SessionRuntimeState.Connected ||
                transportState != TransportState.Connected ||
                !string.Equals(GetApprovedSessionIdForLiveness(), sessionIdSnapshot, StringComparison.Ordinal))
            {
                return;
            }

            LocalOperationalLog.Error(
                "Session",
                $"event=session_liveness_timeout; session_id={sessionIdSnapshot}; generation={generation}; silence_ms={(long)silence.TotalMilliseconds}; terminal_timeout_ms={(long)watchdogOptions.SessionLivenessTimeout.TotalMilliseconds}; role={role}; run_id={GetRunIdForLog()}; scenario={GetScenarioForLog()}");

            await SendPeerVisibilityNoticeForSessionLivenessTimeoutAsync().ConfigureAwait(false);

            CancelSessionLivenessWatchdog("session_liveness_timeout");
            lastDisconnectWasRemoteEnd = false;
            pendingJoinRequest = null;
            InvalidateSessionSecurity("session_liveness_timeout");
            SessionTimeline.Record("Disconnected", "connection_lost");
            PublishSessionFlowEvent(new SessionFlowEvent(
                SessionFlowEventKind.TransportDisconnected,
                role,
                state,
                transportState,
                "session_liveness_timeout"));
            TransitionTo(TransportState.Failed, "session_liveness_timeout");
            SetState(SessionRuntimeState.Failed, "Connection lost.");
            QueueTerminalizeAndDetachFileTransferTransportForPeerDisconnected("session_liveness_timeout");
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task SendPeerVisibilityNoticeForSessionLivenessTimeoutAsync()
    {
        var oldTransport = transport;
        var oldRole = role;
        var oldState = state;
        LocalOperationalLog.Info(
            "Session",
            $"event=session_liveness_timeout_peer_notice_started; role={oldRole}; state={oldState}; transport={(oldTransport is null ? "(none)" : oldTransport.GetType().Name)}");

        var transferNoticeTask = fileTransferService.BroadcastActiveTransferSessionEndNoticeAsync(
            "session_end",
            "session_liveness_timeout",
            CancellationToken.None);
        var sessionEndTask = TrySendRemoteSessionEndAsync(
            oldTransport,
            oldRole,
            oldState,
            reason: "session_liveness_timeout",
            timeout: TimeSpan.FromMilliseconds(1500));

        var transferCount = 0;
        try
        {
            await sessionEndTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn(
                "Session",
                $"event=session_liveness_timeout_session_end_notice_failed; error={ex.GetType().Name}");
        }

        try
        {
            transferCount = await transferNoticeTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn(
                "Session",
                $"event=session_liveness_timeout_filetransfer_notice_failed; error={ex.GetType().Name}");
        }

        LocalOperationalLog.Info(
            "Session",
            $"event=session_liveness_timeout_peer_notice_completed; filetransfer_notice_count={transferCount}");
    }

    private void CancelSessionLivenessWatchdog(string reason)
    {
        CancellationTokenSource? toCancel = null;
        lock (sessionLivenessGate)
        {
            if (sessionLivenessCts is not null)
            {
                toCancel = sessionLivenessCts;
                sessionLivenessCts = null;
            }

            sessionLivenessSuspectLogged = false;
            sessionLivenessHeartbeatInFlight = 0;
            sessionLivenessFileTransferRecoveryDeferralCount = 0;
            sessionLivenessFileTransferBridgeRecoveryDeferralCount = 0;
            sessionLivenessRuntimeUnlockStartupDeferralCount = 0;
            sessionLivenessRecoveryContractDeferralGeneration = 0;
            sessionLivenessFileTransferRecoveryDeferralUntilUtc = DateTimeOffset.MinValue;
            sessionLivenessFileTransferRecoveryDeferralKey = null;
        }

        if (toCancel is null)
        {
            return;
        }

        LocalOperationalLog.Info(
            "Session",
            $"event=session_liveness_watchdog_cancelled; reason={reason}; generation={Volatile.Read(ref sessionLivenessGeneration)}; role={role}; session_id={GetSessionIdForLog()}; run_id={GetRunIdForLog()}; scenario={GetScenarioForLog()}");
        try
        {
            toCancel.Cancel();
        }
        catch
        {
            // Best-effort.
        }
    }

    private void OnSessionLivenessProofReceived(object? sender, SessionLivenessProofEventArgs e)
    {
        if (!IsFromCurrentTransport(sender) ||
            state != SessionRuntimeState.Connected ||
            transportState != TransportState.Connected)
        {
            return;
        }

        var expectedSessionId = GetApprovedSessionIdForLiveness();
        if (string.IsNullOrWhiteSpace(expectedSessionId) ||
            !string.Equals(expectedSessionId, e.SessionId, StringComparison.Ordinal))
        {
            return;
        }

        lock (sessionLivenessGate)
        {
            if (sessionLivenessCts is null)
            {
                return;
            }

            sessionLivenessLastPeerProofUtc = nowProvider();
            sessionLivenessConsecutiveSendFailures = 0;
            sessionLivenessFileTransferRecoveryDeferralCount = 0;
            sessionLivenessFileTransferBridgeRecoveryDeferralCount = 0;
            sessionLivenessRuntimeUnlockStartupDeferralCount = 0;
            sessionLivenessRecoveryContractDeferralGeneration = 0;
            sessionLivenessFileTransferRecoveryDeferralUntilUtc = DateTimeOffset.MinValue;
            sessionLivenessFileTransferRecoveryDeferralKey = null;
            sessionLivenessSuspectLogged = false;
        }

        LocalOperationalLog.Info(
            "Session",
            $"event=session_liveness_peer_proof_observed; session_id={e.SessionId}; generation={e.Generation}; sequence={e.Sequence}; proof_kind={e.ProofKind}; lane={e.Lane}; role={role}; run_id={GetRunIdForLog()}; scenario={GetScenarioForLog()}");
    }

    private string? GetApprovedSessionIdForLiveness()
    {
        return currentSessionGrant?.SessionId.Value ??
               sessionSecurityState.SessionId?.Value;
    }
}
