using System;
using System.Threading;
using System.Threading.Tasks;
using NLink.Core;
using NLink.Core.Logging;
using NLink.Core.Resources;

namespace NLink.App.Services;

public sealed partial class SessionRuntime
{
    private readonly object sessionLivenessGate = new();
    private CancellationTokenSource? sessionLivenessCts;
    private long sessionLivenessGeneration;
    private long sessionLivenessSequence;
    private DateTimeOffset sessionLivenessLastPeerProofUtc;
    private DateTimeOffset sessionLivenessLastAckUtc;
    private int sessionLivenessHeartbeatInFlight;
    private int sessionLivenessConsecutiveSendFailures;
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
            suspectAlreadyLogged = sessionLivenessSuspectLogged;
        }

        var now = nowProvider();
        var silence = now - lastProof;
        if (silence >= watchdogOptions.SessionLivenessTimeout)
        {
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
