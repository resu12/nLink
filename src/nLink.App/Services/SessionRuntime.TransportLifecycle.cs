using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NLink.Core;
using NLink.Core.Diagnostics;
using NLink.Core.Logging;
using NLink.Core.RemoteControl;
using NLink.Core.Resources;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Core.ScreenShare;
using NLink.Infra.Nkn;

namespace NLink.App.Services;

public sealed partial class SessionRuntime
{
    private sealed class SessionTransportLifecycle
    {
        private readonly SessionRuntime owner;

        public SessionTransportLifecycle(SessionRuntime owner)
        {
            this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        }

        public ISignalingTransport AcquireTransportForNewSession(out bool reusedCachedBridge)
        {
            reusedCachedBridge = false;
            CancelCachedBridgeIdleTimeout();

            if (owner.bridgeReusePolicy.IsKeepAlive && owner.cachedBridgeTransport is { } cached)
            {
                owner.cachedBridgeTransport = null;
                reusedCachedBridge = true;
                return cached;
            }

            return owner.createTransport();
        }

        public void EmitSyntheticWarmBridgeLifecycle()
        {
            if (owner.transport is not NknSignalingTransport)
            {
                return;
            }

            owner.OnBridgeLifecycle(owner, new BridgeLifecycleEvent(
                Kind: BridgeLifecycleEventKind.Spawned,
                StartMode: BridgeStartMode.Warm,
                Pid: null,
                ReadyTimeMs: null,
                PingRttMs: null,
                UptimeMs: null,
                ExitCode: null,
                ExitReasonKind: null,
                ExitReasonText: string.Empty));

            owner.OnBridgeLifecycle(owner, new BridgeLifecycleEvent(
                Kind: BridgeLifecycleEventKind.Ready,
                StartMode: BridgeStartMode.Warm,
                Pid: null,
                ReadyTimeMs: 0d,
                PingRttMs: 0d,
                UptimeMs: null,
                ExitCode: null,
                ExitReasonKind: null,
                ExitReasonText: string.Empty));
        }

        public bool ShouldKeepBridgeAlive(ISignalingTransport transportToRelease)
        {
            return !owner.disposed &&
                   owner.bridgeReusePolicy.IsKeepAlive &&
                   transportToRelease is NknSignalingTransport;
        }

        public void CacheTransportForKeepAlive(ISignalingTransport transportToCache)
        {
            if (owner.cachedBridgeTransport is not null)
            {
                try
                {
                    owner.cachedBridgeTransport.Dispose();
                }
                catch
                {
                    // Best-effort replacement cleanup.
                }
                finally
                {
                    owner.cachedBridgeTransport = null;
                }
            }

            owner.cachedBridgeTransport = transportToCache;
            StartCachedBridgeIdleTimeout();
        }

        public void DiscardCachedBridgeTransport()
        {
            CancelCachedBridgeIdleTimeout();

            if (owner.cachedBridgeTransport is not { } cached)
            {
                return;
            }

            owner.cachedBridgeTransport = null;
            try
            {
                cached.Dispose();
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }

        public void WireTransport(ISignalingTransport nextTransport)
        {
            nextTransport.IncomingJoinRequest += owner.OnIncomingJoinRequest;
            if (nextTransport is IHelpRequestSignalingTransport helpRequestTransport)
            {
                helpRequestTransport.IncomingHelpRequest += owner.OnIncomingHelpRequest;
                helpRequestTransport.HelpRequestDecisionReceived += owner.OnHelpRequestDecisionReceived;
            }
            nextTransport.Approved += owner.OnTransportApproved;
            nextTransport.Rejected += owner.OnTransportRejected;
            nextTransport.Disconnected += owner.OnTransportDisconnected;
            if (nextTransport is ISessionSecuritySignalingTransport securityTransport)
            {
                securityTransport.SessionSecurityStateChanged += owner.OnTransportSessionSecurityStateChanged;
                owner.ApplyTransportSecurityState(securityTransport.CurrentSessionSecurityState);
            }

            if (nextTransport is IRemoteControlSignalingTransport controlTransport)
            {
                controlTransport.RemoteControlRequestReceived += owner.OnRemoteControlRequestReceived;
                controlTransport.RemoteControlResponseReceived += owner.OnRemoteControlResponseReceived;
                controlTransport.RemoteControlStartReceived += owner.OnRemoteControlStartReceived;
                controlTransport.RemoteControlStopReceived += owner.OnRemoteControlStopReceived;
                controlTransport.RemoteControlInputReceived += owner.OnRemoteControlInputReceived;
                controlTransport.RemoteControlAckReceived += owner.OnRemoteControlAckReceived;
                controlTransport.RemoteControlStateSnapshotReceived += owner.OnRemoteControlStateSnapshotReceived;
                controlTransport.RemoteControlDisplayInfoReceived += owner.OnRemoteControlDisplayInfoReceived;
            }

            if (nextTransport is IScreenShareSignalingTransport screenShareTransport)
            {
                screenShareTransport.ScreenShareFrameCompleted += owner.OnTransportScreenShareFrameCompleted;
                screenShareTransport.ScreenShareStopped += owner.OnTransportScreenShareStopped;
            }

            if (nextTransport is NknSignalingTransport nknTransport)
            {
                nknTransport.RemoteSessionEnded += owner.OnRemoteSessionEnded;
                nknTransport.BridgeLifecycle += owner.OnBridgeLifecycle;
            }
        }

        public void UnwireTransport(ISignalingTransport nextTransport)
        {
            nextTransport.IncomingJoinRequest -= owner.OnIncomingJoinRequest;
            if (nextTransport is IHelpRequestSignalingTransport helpRequestTransport)
            {
                helpRequestTransport.IncomingHelpRequest -= owner.OnIncomingHelpRequest;
                helpRequestTransport.HelpRequestDecisionReceived -= owner.OnHelpRequestDecisionReceived;
            }
            nextTransport.Approved -= owner.OnTransportApproved;
            nextTransport.Rejected -= owner.OnTransportRejected;
            nextTransport.Disconnected -= owner.OnTransportDisconnected;
            if (nextTransport is ISessionSecuritySignalingTransport securityTransport)
            {
                securityTransport.SessionSecurityStateChanged -= owner.OnTransportSessionSecurityStateChanged;
            }

            if (nextTransport is IRemoteControlSignalingTransport controlTransport)
            {
                controlTransport.RemoteControlRequestReceived -= owner.OnRemoteControlRequestReceived;
                controlTransport.RemoteControlResponseReceived -= owner.OnRemoteControlResponseReceived;
                controlTransport.RemoteControlStartReceived -= owner.OnRemoteControlStartReceived;
                controlTransport.RemoteControlStopReceived -= owner.OnRemoteControlStopReceived;
                controlTransport.RemoteControlInputReceived -= owner.OnRemoteControlInputReceived;
                controlTransport.RemoteControlAckReceived -= owner.OnRemoteControlAckReceived;
                controlTransport.RemoteControlStateSnapshotReceived -= owner.OnRemoteControlStateSnapshotReceived;
                controlTransport.RemoteControlDisplayInfoReceived -= owner.OnRemoteControlDisplayInfoReceived;
            }

            if (nextTransport is IScreenShareSignalingTransport screenShareTransport)
            {
                screenShareTransport.ScreenShareFrameCompleted -= owner.OnTransportScreenShareFrameCompleted;
                screenShareTransport.ScreenShareStopped -= owner.OnTransportScreenShareStopped;
            }

            if (nextTransport is NknSignalingTransport nknTransport)
            {
                nknTransport.RemoteSessionEnded -= owner.OnRemoteSessionEnded;
                nknTransport.BridgeLifecycle -= owner.OnBridgeLifecycle;
            }
        }

        public bool IsFromCurrentTransport(object? sender)
        {
            return sender is null || ReferenceEquals(sender, owner.transport);
        }

        public bool IsKnownBridgeEventSender(object? sender)
        {
            return sender is null ||
                   ReferenceEquals(sender, owner.transport) ||
                   ReferenceEquals(sender, owner.cachedBridgeTransport);
        }

        public void TransitionTo(TransportState newState, string reason, Exception? ex = null)
        {
            var previous = owner.transportState;
            if (!IsTransportTransitionAllowed(previous, newState))
            {
                ThrowInvalidTransportTransition(previous, newState, reason);

                LocalOperationalLog.Error(
                    "Session",
                    $"event=transport_state_transition_blocked; from={previous}; to={newState}; reason={reason}; ex={ex?.GetType().Name ?? "(none)"}; attempt={owner.connectAttempt}; transport={GetCurrentTransportKind()}; run_id={owner.GetRunIdForLog()}; session_id={owner.GetSessionIdForLog()}; scenario={owner.GetScenarioForLog()}");
                return;
            }

            owner.EnsureRemoteControlStoppedForTransportState(newState, reason);
            HandleTimingBeforeStateChange(previous, newState, reason, ex);
            owner.transportState = newState;
            owner.transportStateEntryTimestamps[newState] = Stopwatch.GetTimestamp();
            HandleTimingAfterStateChange(newState);
            UpdateWatchdogForState(newState, reason);
            UpdateTransientStatusForTransportState(newState);
            LocalOperationalLog.Info(
                "Session",
                $"event=transport_state_changed; from={previous}; to={newState}; reason={reason}; ex={ex?.GetType().Name ?? "(none)"}; attempt={owner.connectAttempt}; transport={GetCurrentTransportKind()}; bridge_reuse_mode={owner.GetBridgeReuseModeForLog()}; run_id={owner.GetRunIdForLog()}; session_id={owner.GetSessionIdForLog()}; scenario={owner.GetScenarioForLog()}");
            owner.telemetrySink.OnStateChanged(new TransportStateChangedTelemetryEvent(
                previous,
                newState,
                reason,
                owner.GetRunIdForLog(),
                owner.GetScenarioForTelemetry(),
                owner.GetBridgeReuseModeForTelemetry(),
                owner.connectAttempt,
                GetCurrentTransportKind(),
                owner.GetSessionIdForLog()));
            owner.RefreshSessionFlowProjection();
        }

        public void UpdateTransientStatusForTransportState(TransportState stateValue)
        {
            switch (stateValue)
            {
                case TransportState.BridgeStarting:
                case TransportState.BridgeReady:
                case TransportState.TransportInitializing:
                case TransportState.Connecting:
                case TransportState.Handshake:
                    SetTransientStatus(
                        isVisible: true,
                        text: owner.connectAttempt > 0 ? $"Connecting… (attempt {owner.connectAttempt})" : "Connecting…",
                        canCancel: true);
                    break;
                case TransportState.Reconnecting:
                    SetTransientStatus(
                        isVisible: true,
                        text: owner.connectAttempt > 0 ? $"Reconnecting… (attempt {owner.connectAttempt})" : "Reconnecting…",
                        canCancel: true);
                    break;
                default:
                    SetTransientStatus(isVisible: false, text: string.Empty, canCancel: false);
                    break;
            }
        }

        public void SetTransientStatus(bool isVisible, string text, bool canCancel)
        {
            text ??= string.Empty;
            var changed =
                owner.transientStatusVisible != isVisible ||
                !string.Equals(owner.transientStatusText, text, StringComparison.Ordinal) ||
                owner.transientStatusCanCancel != canCancel;

            owner.transientStatusVisible = isVisible;
            owner.transientStatusText = text;
            owner.transientStatusCanCancel = canCancel;

            if (!changed)
            {
                return;
            }

            RaiseTransientStatusChanged();
        }

        public void RaiseTransientStatusChanged()
        {
            owner.TransientStatusChanged?.Invoke(
                owner,
                new SessionRuntimeTransientStatusChangedEventArgs(
                    owner.transientStatusVisible,
                    owner.transientStatusText,
                    owner.transientStatusCanCancel));
        }

        public void HandleTimingAfterStateChange(TransportState newState)
        {
            switch (newState)
            {
                case TransportState.TransportInitializing:
                    owner.transportInitTiming = TimingSpan.StartNew();
                    if (!owner.connectTiming.IsStarted)
                    {
                        owner.connectTiming = TimingSpan.StartNew();
                    }
                    break;
                case TransportState.BridgeStarting:
                    owner.bridgeStartTiming = TimingSpan.StartNew();
                    break;
                case TransportState.Handshake:
                    owner.handshakeTiming = TimingSpan.StartNew();
                    break;
                case TransportState.Reconnecting:
                    owner.reconnectTiming = TimingSpan.StartNew();
                    break;
            }
        }

        public void UpdateWatchdogForState(TransportState newState, string reason)
        {
            CancelWatchdog();

            if (!owner.watchdogOptions.Enabled)
            {
                return;
            }

            var timeout = GetWatchdogTimeout(newState, reason);
            if (timeout is null || timeout.Value <= TimeSpan.Zero)
            {
                return;
            }

            var cts = new CancellationTokenSource();
            var generation = Interlocked.Increment(ref owner.watchdogGeneration);
            lock (owner.watchdogGate)
            {
                owner.watchdogCts = cts;
            }

            var attempt = owner.connectAttempt;
            var sessionIdSnapshot = owner.GetSessionIdForLog();
            LocalOperationalLog.Info(
                "Session",
                $"event=transport_watchdog_started; state={newState}; timeout_ms={timeout.Value.TotalMilliseconds:F0}; reason={reason}; attempt={attempt}; transport={GetCurrentTransportKind()}; run_id={owner.GetRunIdForLog()}; session_id={sessionIdSnapshot}; scenario={owner.GetScenarioForLog()}");

            _ = Task.Run(async () =>
            {
                try
                {
                    await owner.watchdogDelayAsync(timeout.Value, cts.Token).ConfigureAwait(false);
                    await owner.HandleWatchdogTimeoutAsync(newState, generation, attempt, timeout.Value).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected on normal transitions/reset/dispose.
                }
                catch (Exception watchdogEx)
                {
                    LocalOperationalLog.Error(
                        "Session",
                        $"event=transport_watchdog_internal_error; state={newState}; ex={watchdogEx.GetType().Name}; attempt={attempt}; transport={GetCurrentTransportKind()}; run_id={owner.GetRunIdForLog()}; session_id={sessionIdSnapshot}; scenario={owner.GetScenarioForLog()}");
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
            });
        }

        public TimeSpan? GetWatchdogTimeout(TransportState state, string reason)
        {
            if (state == TransportState.Connecting &&
                (owner.role == SessionRuntimeRole.Helpee || owner.IsPassiveHelperListenerState()))
            {
                return null;
            }

            return state switch
            {
                TransportState.BridgeStarting => owner.watchdogOptions.BridgeStartingTimeout,
                TransportState.Connecting => owner.watchdogOptions.ConnectingTimeout,
                TransportState.Handshake => owner.watchdogOptions.HandshakeTimeout,
                TransportState.Reconnecting => owner.watchdogOptions.ReconnectingTimeout,
                _ => null
            };
        }

        public void CancelWatchdog()
        {
            CancellationTokenSource? toCancel = null;
            lock (owner.watchdogGate)
            {
                if (owner.watchdogCts is not null)
                {
                    toCancel = owner.watchdogCts;
                    owner.watchdogCts = null;
                }
            }

            if (toCancel is null)
            {
                return;
            }

            try
            {
                toCancel.Cancel();
            }
            catch
            {
                // Best-effort.
            }
        }

        public void StartCachedBridgeIdleTimeout()
        {
            CancelCachedBridgeIdleTimeout();

            if (!owner.bridgeReusePolicy.IsKeepAlive ||
                owner.bridgeReusePolicy.KeepAliveIdleTimeout <= TimeSpan.Zero ||
                owner.cachedBridgeTransport is null)
            {
                return;
            }

            var cts = new CancellationTokenSource();
            var generation = Interlocked.Increment(ref owner.cachedBridgeIdleGeneration);
            owner.cachedBridgeIdleCts = cts;

            ActiveRuntimeCounters.IncRetryTimers();
            owner.RunCountedBackgroundTask(async () =>
            {
                try
                {
                    await owner.bridgeIdleDelayAsync(owner.bridgeReusePolicy.KeepAliveIdleTimeout, cts.Token).ConfigureAwait(false);
                    await HandleCachedBridgeIdleTimeoutAsync(generation).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected.
                }
                catch (Exception ex)
                {
                    LocalOperationalLog.Error(
                        "Session",
                        $"event=bridge_idle_timeout_internal_error; ex={ex.GetType().Name}; attempt={owner.connectAttempt}; transport={GetCurrentTransportKind()}; bridge_reuse_mode={owner.GetBridgeReuseModeForLog()}; run_id={owner.GetRunIdForLog()}; session_id={owner.GetSessionIdForLog()}; scenario={owner.GetScenarioForLog()}");
                }
                finally
                {
                    ActiveRuntimeCounters.DecRetryTimers();
                }
            });
        }

        public void CancelCachedBridgeIdleTimeout()
        {
            var cts = owner.cachedBridgeIdleCts;
            owner.cachedBridgeIdleCts = null;
            if (cts is null)
            {
                return;
            }

            try
            {
                cts.Cancel();
            }
            catch
            {
                // Best-effort.
            }
            finally
            {
                cts.Dispose();
            }
        }

        public async Task HandleCachedBridgeIdleTimeoutAsync(long generation)
        {
            ISignalingTransport? toDispose = null;

            await owner.lifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (owner.disposed || owner.resetInProgress)
                {
                    return;
                }

                if (generation != Volatile.Read(ref owner.cachedBridgeIdleGeneration))
                {
                    return;
                }

                if (owner.transport is not null)
                {
                    return;
                }

                toDispose = owner.cachedBridgeTransport;
                owner.cachedBridgeTransport = null;
                owner.cachedBridgeIdleCts?.Dispose();
                owner.cachedBridgeIdleCts = null;

                if (toDispose is null)
                {
                    return;
                }

                LocalOperationalLog.Info(
                    "Session",
                    $"event=bridge_killed; reason=idle_timeout; attempt={owner.connectAttempt}; transport=NKN; bridge_reuse_mode={owner.GetBridgeReuseModeForLog()}; run_id={owner.GetRunIdForLog()}; session_id={owner.GetSessionIdForLog()}; scenario={owner.GetScenarioForLog()}");
                owner.telemetrySink.OnBridgeLifecycle(new BridgeLifecycleTelemetryEvent(
                    EventName: "bridge_exited",
                    StartMode: string.Empty,
                    Pid: null,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReason: "killed",
                    RunId: owner.GetRunIdForLog(),
                    Scenario: owner.GetScenarioForTelemetry(),
                    BridgeReuseMode: owner.GetBridgeReuseModeForTelemetry(),
                    Attempt: owner.connectAttempt,
                    Transport: "NKN",
                    SessionId: owner.GetSessionIdForLog()));
            }
            finally
            {
                owner.lifecycleGate.Release();
            }

            if (toDispose is not null)
            {
                try
                {
                    await Task.Run(toDispose.Dispose).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort idle cleanup.
                }
            }
        }

        public void HandleTimingBeforeStateChange(
            TransportState previous,
            TransportState next,
            string reason,
            Exception? ex)
        {
            if (previous == TransportState.TransportInitializing &&
                next != TransportState.TransportInitializing &&
                owner.transportInitTiming.IsStarted)
            {
                CompleteDurationMetric("transport_init_duration_ms", "transport_init_completed", owner.transportInitTiming, reason, ex, next == TransportState.Failed);
                owner.transportInitTiming = default;
            }

            if (previous == TransportState.BridgeStarting &&
                next != TransportState.BridgeStarting &&
                owner.bridgeStartTiming.IsStarted)
            {
                CompleteDurationMetric("bridge_start_duration_ms", "bridge_start_completed", owner.bridgeStartTiming, reason, ex, next == TransportState.Failed);
                owner.bridgeStartTiming = default;
            }

            if (previous == TransportState.Handshake &&
                next != TransportState.Handshake &&
                owner.handshakeTiming.IsStarted)
            {
                CompleteDurationMetric("handshake_duration_ms", "handshake_completed", owner.handshakeTiming, reason, ex, next == TransportState.Failed);
                owner.handshakeTiming = default;
            }

            if (previous == TransportState.Reconnecting &&
                next != TransportState.Reconnecting &&
                owner.reconnectTiming.IsStarted)
            {
                CompleteDurationMetric("reconnect_duration_ms", "reconnect_completed", owner.reconnectTiming, reason, ex, next == TransportState.Failed);
                owner.reconnectTiming = default;
            }

            var connectCompletes =
                owner.connectTiming.IsStarted &&
                (next == TransportState.Connected ||
                 next == TransportState.Failed ||
                 next == TransportState.Disposed);

            if (connectCompletes)
            {
                CompleteDurationMetric("connect_duration_ms", "connect_completed", owner.connectTiming, reason, ex, next != TransportState.Connected);
                owner.connectTiming = default;
            }
        }

        public void CompleteDurationMetric(
            string metricName,
            string eventName,
            TimingSpan timing,
            string reason,
            Exception? ex,
            bool failed)
        {
            var durationMs = timing.ElapsedMilliseconds();
            if (durationMs < 0)
            {
                durationMs = 0;
            }

            owner.lastDurationMetricsMs[metricName] = durationMs;
            var transportKind = GetCurrentTransportKind();
            var sessionIdForLog = owner.GetSessionIdForLog();
            LocalOperationalLog.Info(
                "Session",
                $"event={eventName}; duration_ms={durationMs:F2}; attempt={owner.connectAttempt}; transport={transportKind}; bridge_reuse_mode={owner.GetBridgeReuseModeForLog()}; run_id={owner.GetRunIdForLog()}; session_id={sessionIdForLog}; scenario={owner.GetScenarioForLog()}; outcome={(failed ? "failed" : "success")}; reason={reason}; ex={ex?.GetType().Name ?? "(none)"}");
            owner.telemetrySink.OnTimingCompleted(new TransportTimingCompletedTelemetryEvent(
                eventName,
                metricName,
                durationMs,
                failed,
                reason,
                owner.GetRunIdForLog(),
                owner.GetScenarioForTelemetry(),
                owner.GetBridgeReuseModeForTelemetry(),
                owner.connectAttempt,
                transportKind,
                sessionIdForLog));
        }

        public string GetCurrentTransportKind()
        {
            return owner.transport switch
            {
                null => "(none)",
                NknSignalingTransport => "NKN",
                _ => "DevLocal"
            };
        }
    }
}
