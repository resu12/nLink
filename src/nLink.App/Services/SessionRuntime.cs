using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NLink.Core;
using NLink.Core.Chat;
using NLink.Core.Logging;
using NLink.Infra.Nkn;

namespace NLink.App.Services;

public enum SessionRuntimeRole
{
    None,
    Helpee,
    Helper,
}

public enum TransportState
{
    Idle,
    BridgeStarting,
    BridgeReady,
    TransportInitializing,
    Connecting,
    Handshake,
    Connected,
    Reconnecting,
    Failed,
    Disposed,
}

public enum SessionRuntimeState
{
    Idle,
    Waiting,
    IncomingJoinRequest,
    Connecting,
    Connected,
    Rejected,
    Failed,
    Disconnected,
}

public sealed class SessionRuntimeStateChangedEventArgs : EventArgs
{
    public SessionRuntimeStateChangedEventArgs(
        SessionRuntimeState state,
        SessionRuntimeRole role,
        string statusText,
        SessionCode? currentCode)
    {
        State = state;
        Role = role;
        StatusText = statusText;
        CurrentCode = currentCode;
    }

    public SessionRuntimeState State { get; }

    public SessionRuntimeRole Role { get; }

    public string StatusText { get; }

    public SessionCode? CurrentCode { get; }
}

public readonly record struct DiagnosticsSnapshot(
    string CurrentState,
    string SessionUiState,
    long AttemptNumber,
    string LastFailureCategory,
    string LastFailureMessage,
    double? LastConnectDurationMs,
    double? LastHandshakeDurationMs,
    double? LastBridgeStartDurationMs);

internal sealed record SessionRuntimeWatchdogOptions(
    bool Enabled,
    bool AutoRetryEnabled,
    TimeSpan BridgeStartingTimeout,
    TimeSpan ConnectingTimeout,
    TimeSpan HandshakeTimeout,
    TimeSpan ReconnectingTimeout)
{
    public static SessionRuntimeWatchdogOptions Default { get; } = new(
        Enabled: true,
        AutoRetryEnabled: false,
        BridgeStartingTimeout: TimeSpan.FromSeconds(10),
        ConnectingTimeout: TimeSpan.FromSeconds(30),
        HandshakeTimeout: TimeSpan.FromSeconds(30),
        ReconnectingTimeout: TimeSpan.FromSeconds(10));
}

public sealed class SessionRuntime : IDisposable
{
    private readonly Func<ISignalingTransport> createTransport;
    private readonly SessionChatService chatService = new();
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly Dictionary<TransportState, long> transportStateEntryTimestamps = new();
    private readonly Dictionary<string, double> lastDurationMetricsMs = new(StringComparer.Ordinal);
    private readonly object watchdogGate = new();
    private readonly SessionRuntimeWatchdogOptions watchdogOptions;
    private readonly Func<TimeSpan, CancellationToken, Task> watchdogDelayAsync;
    private readonly ITransportTelemetrySink telemetrySink;
    private readonly BridgeReusePolicy bridgeReusePolicy;
    private readonly Func<TimeSpan, CancellationToken, Task> bridgeIdleDelayAsync;

    private CancellationTokenSource? sessionCts;
    private ISignalingTransport? transport;
    private IncomingJoinRequestEventArgs? pendingJoinRequest;
    private SessionRuntimeRole role;
    private SessionRuntimeState state = SessionRuntimeState.Idle;
    private TransportState transportState = TransportState.Idle;
    private SessionCode? currentCode;
    private string statusText = string.Empty;
    private bool resetInProgress;
    private bool startInProgress;
    private bool remoteSessionEndHandling;
    private bool disposed;
    private long connectAttempt;
    private string sessionId = string.Empty;
    private string attemptSessionKey = string.Empty;
    private TransportFailure? lastTransportFailure;
    private TimingSpan bridgeStartTiming;
    private TimingSpan transportInitTiming;
    private TimingSpan connectTiming;
    private TimingSpan handshakeTiming;
    private TimingSpan reconnectTiming;
    private CancellationTokenSource? watchdogCts;
    private long watchdogGeneration;
    private ISignalingTransport? cachedBridgeTransport;
    private CancellationTokenSource? cachedBridgeIdleCts;
    private long cachedBridgeIdleGeneration;

    public SessionRuntime(Func<ISignalingTransport> createTransport)
        : this(createTransport, SessionRuntimeWatchdogOptions.Default, DefaultWatchdogDelayAsync, TransportTelemetry.Noop, BridgeReusePolicy.Default, null)
    {
    }

    internal SessionRuntime(
        Func<ISignalingTransport> createTransport,
        SessionRuntimeWatchdogOptions? watchdogOptions,
        Func<TimeSpan, CancellationToken, Task>? watchdogDelayAsync = null,
        ITransportTelemetrySink? telemetrySink = null,
        BridgeReusePolicy? bridgeReusePolicy = null,
        Func<TimeSpan, CancellationToken, Task>? bridgeIdleDelayAsync = null)
    {
        this.createTransport = createTransport ?? throw new ArgumentNullException(nameof(createTransport));
        this.watchdogOptions = watchdogOptions ?? SessionRuntimeWatchdogOptions.Default;
        this.watchdogDelayAsync = watchdogDelayAsync ?? DefaultWatchdogDelayAsync;
        this.telemetrySink = telemetrySink ?? TransportTelemetry.Noop;
        this.bridgeReusePolicy = bridgeReusePolicy ?? BridgeReusePolicy.Default;
        this.bridgeIdleDelayAsync = bridgeIdleDelayAsync ?? DefaultWatchdogDelayAsync;
        transportStateEntryTimestamps[transportState] = Stopwatch.GetTimestamp();

        chatService.MessageReceived += OnChatMessageReceived;
        chatService.MessageReceivedBeforeApproved += OnChatMessageReceivedBeforeApproved;
        chatService.StateChanged += OnChatStateChanged;
    }

    private static Task DefaultWatchdogDelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);

    public event EventHandler<SessionRuntimeStateChangedEventArgs>? StateChanged;

    public event EventHandler? IncomingJoinRequestAvailable;

    public event EventHandler? Approved;

    public event EventHandler? Rejected;

    public event EventHandler? Disconnected;

    public event EventHandler<ChatMessageEventArgs>? ChatMessageReceived;

    public event EventHandler? ChatMessageReceivedBeforeApproved;

    public event EventHandler? ChatStateChanged;

    public SessionRuntimeState State => state;
    public TransportState TransportLifecycleState => transportState;

    public SessionRuntimeRole Role => role;

    public string StatusText => statusText;

    public SessionCode? CurrentCode => currentCode;

    public bool HasPendingJoinRequest => pendingJoinRequest is not null;

    public bool CanSendChat => chatService.CanSend;

    public bool HasSessionKey => chatService.HasSessionKey;

    public bool IsApproved => chatService.IsApproved;

    internal long GetTransportStateEntryTimestamp(TransportState state)
    {
        return transportStateEntryTimestamps.TryGetValue(state, out var ts) ? ts : 0L;
    }

    internal double? GetLastDurationMetricMilliseconds(string metricName)
    {
        return lastDurationMetricsMs.TryGetValue(metricName, out var value) ? value : null;
    }

    internal long GetConnectAttemptForTests() => connectAttempt;

    internal string GetSessionIdForTests() => sessionId;

    internal TransportFailureCategory? GetLastFailureCategoryForTests() => lastTransportFailure?.Category;

    internal bool HasCachedBridgeTransportForTests() => cachedBridgeTransport is not null;

    public DiagnosticsSnapshot GetDiagnosticsSnapshot()
    {
        return new DiagnosticsSnapshot(
            CurrentState: transportState.ToString(),
            SessionUiState: state.ToString(),
            AttemptNumber: connectAttempt,
            LastFailureCategory: lastTransportFailure?.Category.ToString() ?? "(none)",
            LastFailureMessage: string.IsNullOrWhiteSpace(lastTransportFailure?.Message) ? "(none)" : lastTransportFailure!.Message,
            LastConnectDurationMs: GetLastDurationMetricMilliseconds("connect_duration_ms"),
            LastHandshakeDurationMs: GetLastDurationMetricMilliseconds("handshake_duration_ms"),
            LastBridgeStartDurationMs: GetLastDurationMetricMilliseconds("bridge_start_duration_ms"));
    }

    public void SetReliabilityAttempt(SessionReliabilityAttempt? attempt)
    {
        chatService.SetReliabilityAttempt(attempt);
    }

    public async Task StartHelpeeAsync(SessionCode code, CancellationToken uiCt)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        await lifecycleGate.WaitAsync(uiCt);
        try
        {
            ThrowIfStartInProgress();
            startInProgress = true;

            await ResetCoreAsync(notifyRemoteSessionEnd: false).ConfigureAwait(false);
            BeginConnectAttempt(SessionRuntimeRole.Helpee, code);
            TransitionTo(TransportState.TransportInitializing, "start_helpee");

            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(uiCt);
            var nextTransport = AcquireTransportForNewSession(out var reusedCachedBridge);
            sessionCts = linkedCts;
            transport = nextTransport;
            role = SessionRuntimeRole.Helpee;
            currentCode = code;
            pendingJoinRequest = null;
            SessionTimeline.Record("Started");
            SessionTimeline.Record("Hosting");

            WireTransport(nextTransport);
            chatService.AttachTransport(nextTransport);
            if (nextTransport is NknSignalingTransport)
            {
                TransitionTo(TransportState.BridgeStarting, "nkn_bridge_starting");
                if (reusedCachedBridge)
                {
                    EmitSyntheticWarmBridgeLifecycle();
                }
            }
            else
            {
                TransitionTo(TransportState.Connecting, "host_start");
            }

            SetState(SessionRuntimeState.Waiting, "Waiting for helper…");

            _ = RunHostAsync(nextTransport, code, linkedCts.Token);
        }
        finally
        {
            startInProgress = false;
            lifecycleGate.Release();
        }
    }

    public async Task StartHelperAsync(SessionCode code, CancellationToken uiCt)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        await lifecycleGate.WaitAsync(uiCt);
        try
        {
            ThrowIfStartInProgress();
            startInProgress = true;

            await ResetCoreAsync(notifyRemoteSessionEnd: false).ConfigureAwait(false);
            BeginConnectAttempt(SessionRuntimeRole.Helper, code);
            TransitionTo(TransportState.TransportInitializing, "start_helper");

            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(uiCt);
            var nextTransport = AcquireTransportForNewSession(out var reusedCachedBridge);
            sessionCts = linkedCts;
            transport = nextTransport;
            role = SessionRuntimeRole.Helper;
            currentCode = code;
            pendingJoinRequest = null;
            SessionTimeline.Record("Started");
            SessionTimeline.Record("Joining");

            WireTransport(nextTransport);
            chatService.AttachTransport(nextTransport);
            if (nextTransport is NknSignalingTransport)
            {
                TransitionTo(TransportState.BridgeStarting, "nkn_bridge_starting");
                if (reusedCachedBridge)
                {
                    EmitSyntheticWarmBridgeLifecycle();
                }
            }
            else
            {
                TransitionTo(TransportState.Connecting, "join_start");
            }

            SetState(SessionRuntimeState.Connecting, "Connecting…");

            await nextTransport.JoinAsync(code, linkedCts.Token).ConfigureAwait(false);
            TransitionTo(TransportState.Handshake, "join_request_sent");
        }
        finally
        {
            startInProgress = false;
            lifecycleGate.Release();
        }
    }

    public async Task ApproveAsync(CancellationToken uiCt)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        IncomingJoinRequestEventArgs? request;
        await lifecycleGate.WaitAsync(uiCt).ConfigureAwait(false);
        try
        {
            request = pendingJoinRequest;
            pendingJoinRequest = null;
            if (request is null)
            {
                return;
            }

            TransitionTo(TransportState.Handshake, "local_approve");
            SetState(SessionRuntimeState.Connected, "Connected");
        }
        finally
        {
            lifecycleGate.Release();
        }

        try
        {
            await request.ApproveAsync(uiCt).ConfigureAwait(false);
        }
        catch
        {
            // UI state is already optimistic; transport disconnect will reconcile if needed.
        }
    }

    public async Task RejectAsync(CancellationToken uiCt)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        IncomingJoinRequestEventArgs? request;
        await lifecycleGate.WaitAsync(uiCt).ConfigureAwait(false);
        try
        {
            request = pendingJoinRequest;
            pendingJoinRequest = null;
            if (request is null)
            {
                return;
            }

            TransitionTo(TransportState.Failed, "local_reject");
            SetState(SessionRuntimeState.Rejected, "Permission was declined.");
        }
        finally
        {
            lifecycleGate.Release();
        }

        try
        {
            await request.RejectAsync(uiCt).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort.
        }
    }

    public Task<ChatMessageRecord?> TrySendChatTextAsync(string text, CancellationToken uiCt)
    {
        return chatService.TrySendTextAsync(text, uiCt);
    }

    public Task SendChatAsync(ReadOnlyMemory<byte> payload, CancellationToken uiCt)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var currentTransport = transport ?? throw new InvalidOperationException("No active session.");
        return currentTransport.SendChatMessageAsync(payload, uiCt);
    }

    public Task DisconnectAsync()
    {
        return ResetAsync(notifyRemoteSessionEnd: true);
    }

    public async Task FailAsync(string userStatusText)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ResetCoreAsync(notifyRemoteSessionEnd: false).ConfigureAwait(false);
            TransitionTo(TransportState.Failed, "fail_async");
            SetState(SessionRuntimeState.Failed, userStatusText ?? string.Empty);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task FailAsync(TransportFailure failure, string userStatusText)
    {
        ArgumentNullException.ThrowIfNull(failure);
        await FailAsync(userStatusText).ConfigureAwait(false);
        LogTransportFailure(failure, "fail_async");
    }

    public Task ResetAsync()
    {
        return ResetAsync(notifyRemoteSessionEnd: true);
    }

    private async Task ResetAsync(bool notifyRemoteSessionEnd)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ResetCoreAsync(notifyRemoteSessionEnd).ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        chatService.MessageReceived -= OnChatMessageReceived;
        chatService.MessageReceivedBeforeApproved -= OnChatMessageReceivedBeforeApproved;
        chatService.StateChanged -= OnChatStateChanged;

        try
        {
            DisconnectAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Best-effort during shutdown.
        }

        disposed = true;
        TransitionTo(TransportState.Disposed, "dispose");
        CancelCachedBridgeIdleTimeout();

        if (cachedBridgeTransport is not null)
        {
            try
            {
                cachedBridgeTransport.Dispose();
            }
            catch
            {
                // Best-effort shutdown.
            }
            finally
            {
                cachedBridgeTransport = null;
            }
        }

        chatService.Dispose();
        lifecycleGate.Dispose();
    }

    private async Task RunHostAsync(ISignalingTransport hostTransport, SessionCode code, CancellationToken ct)
    {
        try
        {
            await hostTransport.HostAsync(code, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal during reset/navigation.
        }
        catch
        {
            if (ct.IsCancellationRequested || !ReferenceEquals(transport, hostTransport) || disposed)
            {
                return;
            }

            var lastError = NknRuntimeDiagnostics.Snapshot().LastError;
            var message = UserErrorMapper.IsNknStartFailure(lastError)
                ? UserErrorMapper.NknStartFailedReinstall()
                : UserErrorMapper.HelpeeHostStartFailure();
            var failure = TransportFailureMapper.FromSignals(lastError, lastDisconnectReason: NknRuntimeDiagnostics.Snapshot().LastDisconnectReason, fallbackMessage: message);
            TransitionTo(TransportState.Failed, "host_start_failed");
            SetState(SessionRuntimeState.Disconnected, message);
            LogTransportFailure(failure, "host_start_failed");
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task ResetCoreAsync(bool notifyRemoteSessionEnd)
    {
        if (resetInProgress)
        {
            return;
        }

        resetInProgress = true;
        try
        {
            var oldCts = sessionCts;
            var oldTransport = transport;
            var oldRole = role;
            var oldState = state;
            var hadActiveSession = oldCts is not null || oldTransport is not null || oldRole != SessionRuntimeRole.None;

            if (hadActiveSession && transportState is not TransportState.Disposed)
            {
                TransitionTo(TransportState.Reconnecting, "reset");
            }

            sessionCts = null;
            transport = null;
            pendingJoinRequest = null;
            role = SessionRuntimeRole.None;
            currentCode = null;
            CancelWatchdog();

            if (notifyRemoteSessionEnd)
            {
                await TrySendRemoteSessionEndAsync(oldTransport, oldRole, oldState).ConfigureAwait(false);
            }

            if (oldTransport is not null)
            {
                UnwireTransport(oldTransport);
            }

            chatService.DetachTransport();

            if (oldCts is not null)
            {
                try
                {
                    oldCts.Cancel();
                }
                catch
                {
                    // Ignore.
                }
                oldCts.Dispose();
            }

            if (oldTransport is not null)
            {
                if (ShouldKeepBridgeAlive(oldTransport))
                {
                    if (oldTransport is NknSignalingTransport nknTransport)
                    {
                        try
                        {
                            await nknTransport.PrepareForReuseAsync().ConfigureAwait(false);
                        }
                        catch
                        {
                            // If keepalive cleanup fails, fall back to normal disposal.
                            try
                            {
                                await Task.Run(oldTransport.Dispose).ConfigureAwait(false);
                            }
                            catch
                            {
                                // Best-effort cleanup.
                            }

                            oldTransport = null;
                        }
                    }

                    if (oldTransport is not null)
                    {
                        CacheTransportForKeepAlive(oldTransport);
                    }
                }
                else
                {
                    try
                    {
                        await Task.Run(oldTransport.Dispose).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Best-effort cleanup.
                    }
                }
            }

            SetState(SessionRuntimeState.Idle, string.Empty);
            if (!disposed)
            {
                TransitionTo(TransportState.Idle, "reset_complete");
            }
        }
        finally
        {
            resetInProgress = false;
        }
    }

    private ISignalingTransport AcquireTransportForNewSession(out bool reusedCachedBridge)
    {
        reusedCachedBridge = false;
        CancelCachedBridgeIdleTimeout();

        if (bridgeReusePolicy.IsKeepAlive && cachedBridgeTransport is { } cached)
        {
            cachedBridgeTransport = null;
            reusedCachedBridge = true;
            return cached;
        }

        return createTransport();
    }

    private void EmitSyntheticWarmBridgeLifecycle()
    {
        if (transport is not NknSignalingTransport)
        {
            return;
        }

        OnBridgeLifecycle(this, new BridgeLifecycleEvent(
            Kind: BridgeLifecycleEventKind.Spawned,
            StartMode: BridgeStartMode.Warm,
            Pid: null,
            ReadyTimeMs: null,
            PingRttMs: null,
            UptimeMs: null,
            ExitCode: null,
            ExitReasonKind: null,
            ExitReasonText: string.Empty));

        OnBridgeLifecycle(this, new BridgeLifecycleEvent(
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

    private bool ShouldKeepBridgeAlive(ISignalingTransport transportToRelease)
    {
        return !disposed &&
               bridgeReusePolicy.IsKeepAlive &&
               transportToRelease is NknSignalingTransport;
    }

    private void CacheTransportForKeepAlive(ISignalingTransport transportToCache)
    {
        if (cachedBridgeTransport is not null)
        {
            try
            {
                cachedBridgeTransport.Dispose();
            }
            catch
            {
                // Best-effort replacement cleanup.
            }
            finally
            {
                cachedBridgeTransport = null;
            }
        }

        cachedBridgeTransport = transportToCache;
        StartCachedBridgeIdleTimeout();
    }

    private void WireTransport(ISignalingTransport nextTransport)
    {
        nextTransport.IncomingJoinRequest += OnIncomingJoinRequest;
        nextTransport.Approved += OnTransportApproved;
        nextTransport.Rejected += OnTransportRejected;
        nextTransport.Disconnected += OnTransportDisconnected;
        if (nextTransport is NknSignalingTransport nknTransport)
        {
            nknTransport.RemoteSessionEnded += OnRemoteSessionEnded;
            nknTransport.BridgeLifecycle += OnBridgeLifecycle;
        }
    }

    private void UnwireTransport(ISignalingTransport nextTransport)
    {
        nextTransport.IncomingJoinRequest -= OnIncomingJoinRequest;
        nextTransport.Approved -= OnTransportApproved;
        nextTransport.Rejected -= OnTransportRejected;
        nextTransport.Disconnected -= OnTransportDisconnected;
        if (nextTransport is NknSignalingTransport nknTransport)
        {
            nknTransport.RemoteSessionEnded -= OnRemoteSessionEnded;
            nknTransport.BridgeLifecycle -= OnBridgeLifecycle;
        }
    }

    private void OnIncomingJoinRequest(object? sender, IncomingJoinRequestEventArgs e)
    {
        if (disposed || resetInProgress)
        {
            _ = e.RejectAsync();
            return;
        }

        if (pendingJoinRequest is not null)
        {
            _ = e.RejectAsync();
            return;
        }

        pendingJoinRequest = e;
        SessionTimeline.Record("IncomingJoinRequest");
        TransitionTo(TransportState.Handshake, "incoming_join_request");
        SetState(SessionRuntimeState.IncomingJoinRequest, "Helper on this PC wants to connect. Click Allow.");
        IncomingJoinRequestAvailable?.Invoke(this, EventArgs.Empty);
    }

    private void OnTransportApproved(object? sender, EventArgs e)
    {
        SessionTimeline.Record("Approved");
        TransitionTo(TransportState.Connected, "transport_approved");
        SetState(SessionRuntimeState.Connected, "Connected");
        Approved?.Invoke(this, EventArgs.Empty);
    }

    private void OnTransportRejected(object? sender, EventArgs e)
    {
        pendingJoinRequest = null;
        SessionTimeline.Record("Rejected");
        TransitionTo(TransportState.Failed, "transport_rejected");
        SetState(SessionRuntimeState.Rejected, "Permission was declined.");
        Rejected?.Invoke(this, EventArgs.Empty);
    }

    private void OnTransportDisconnected(object? sender, EventArgs e)
    {
        if (disposed || resetInProgress || remoteSessionEndHandling || sessionCts?.IsCancellationRequested == true)
        {
            return;
        }

        var shouldFail = state is SessionRuntimeState.Waiting
            or SessionRuntimeState.IncomingJoinRequest
            or SessionRuntimeState.Connecting
            or SessionRuntimeState.Connected;

        if (shouldFail)
        {
            pendingJoinRequest = null;
            SessionTimeline.Record("Disconnected", "connection_lost");
            var snapshot = NknRuntimeDiagnostics.Snapshot();
            var failure = TransportFailureMapper.FromSignals(
                snapshot.LastError,
                lastDisconnectReason: snapshot.LastDisconnectReason,
                fallbackMessage: "Connection lost.");
            TransitionTo(TransportState.Failed, "transport_disconnected");
            SetState(SessionRuntimeState.Failed, "Connection lost.");
            LogTransportFailure(failure, "transport_disconnected");
        }

        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    private void OnRemoteSessionEnded(object? sender, EventArgs e)
    {
        if (disposed || resetInProgress || remoteSessionEndHandling)
        {
            return;
        }

        remoteSessionEndHandling = true;

        var message = role switch
        {
            SessionRuntimeRole.Helpee => "The helper ended the session.",
            SessionRuntimeRole.Helper => "The other person ended the session.",
            _ => "The session ended."
        };

        _ = Task.Run(async () =>
        {
            try
            {
                SessionTimeline.Record("SessionEndReceived", "remote_end");
                SessionTimeline.Record("Disconnected", "remote_end");
                await FailAsync(message).ConfigureAwait(false);
                Disconnected?.Invoke(this, EventArgs.Empty);
            }
            catch
            {
                // Best-effort. Transport disconnection will still update UI if needed.
            }
            finally
            {
                remoteSessionEndHandling = false;
            }
        });
    }

    private void OnBridgeLifecycle(object? sender, BridgeLifecycleEvent e)
    {
        if (e.Kind == BridgeLifecycleEventKind.Ready &&
            transportState == TransportState.BridgeStarting)
        {
            TransitionTo(TransportState.BridgeReady, "bridge_ready");
            if (role is SessionRuntimeRole.Helper or SessionRuntimeRole.Helpee &&
                transport is NknSignalingTransport &&
                transportState == TransportState.BridgeReady)
            {
                TransitionTo(TransportState.Connecting, "bridge_ready");
            }
        }

        var eventName = e.Kind switch
        {
            BridgeLifecycleEventKind.Spawned => "bridge_spawned",
            BridgeLifecycleEventKind.Ready => "bridge_ready",
            BridgeLifecycleEventKind.Exited => "bridge_exited",
            _ => "bridge_unknown"
        };

        var startMode = e.StartMode?.ToString().ToLowerInvariant() ?? string.Empty;
        var exitReason = e.ExitReasonKind?.ToString().ToLowerInvariant() ?? (string.IsNullOrWhiteSpace(e.ExitReasonText) ? string.Empty : e.ExitReasonText);
        var transportKind = "NKN";
        var sessionIdForLog = GetSessionIdForLog();

        LocalOperationalLog.Info(
            "Session",
            $"event={eventName}; start_mode={(string.IsNullOrWhiteSpace(startMode) ? "(none)" : startMode)}; pid={(e.Pid?.ToString() ?? "(none)")}; ready_time_ms={(e.ReadyTimeMs?.ToString("F2") ?? "(none)")}; ping_rtt_ms={(e.PingRttMs?.ToString("F2") ?? "(none)")}; uptime_ms={(e.UptimeMs?.ToString("F2") ?? "(none)")}; exit_code={(e.ExitCode?.ToString() ?? "(none)")}; exit_reason={(string.IsNullOrWhiteSpace(exitReason) ? "(none)" : exitReason)}; attempt={connectAttempt}; transport={transportKind}; bridge_reuse_mode={GetBridgeReuseModeForLog()}; run_id={GetRunIdForLog()}; session_id={sessionIdForLog}; scenario={GetScenarioForLog()}");

        telemetrySink.OnBridgeLifecycle(new BridgeLifecycleTelemetryEvent(
            eventName,
            startMode,
            e.Pid,
            e.ReadyTimeMs,
            e.PingRttMs,
            e.UptimeMs,
            e.ExitCode,
            exitReason,
            GetRunIdForLog(),
            GetScenarioForTelemetry(),
            GetBridgeReuseModeForTelemetry(),
            connectAttempt,
            transportKind,
            sessionIdForLog));
    }

    private void OnChatMessageReceived(object? sender, ChatMessageEventArgs e)
    {
        ChatMessageReceived?.Invoke(this, e);
    }

    private void OnChatMessageReceivedBeforeApproved(object? sender, EventArgs e)
    {
        ChatMessageReceivedBeforeApproved?.Invoke(this, EventArgs.Empty);
    }

    private void OnChatStateChanged(object? sender, EventArgs e)
    {
        ChatStateChanged?.Invoke(this, EventArgs.Empty);
    }

    internal static bool IsTransportTransitionAllowed(TransportState from, TransportState to)
    {
        if (from == to)
        {
            return true;
        }

        if (from == TransportState.Disposed)
        {
            return false;
        }

        if (to == TransportState.Disposed)
        {
            return true;
        }

        if (to == TransportState.Failed)
        {
            return true;
        }

        if (to == TransportState.Reconnecting)
        {
            return from is not TransportState.Idle;
        }

        return (from, to) switch
        {
            (TransportState.Idle, TransportState.TransportInitializing) => true,
            (TransportState.TransportInitializing, TransportState.BridgeStarting) => true,
            (TransportState.TransportInitializing, TransportState.Connecting) => true,
            (TransportState.BridgeStarting, TransportState.BridgeReady) => true,
            (TransportState.BridgeReady, TransportState.Connecting) => true,
            (TransportState.Connecting, TransportState.Handshake) => true,
            (TransportState.Connecting, TransportState.Connected) => true,
            (TransportState.Handshake, TransportState.Connected) => true,
            (TransportState.Reconnecting, TransportState.Idle) => true,
            (TransportState.Failed, TransportState.Reconnecting) => true,
            (TransportState.Failed, TransportState.Idle) => true,
            (TransportState.Failed, TransportState.TransportInitializing) => true,
            _ => false
        };
    }

    [Conditional("DEBUG")]
    private static void ThrowInvalidTransportTransition(TransportState from, TransportState to, string reason)
    {
        throw new InvalidOperationException(
            $"Invalid transport transition: {from} -> {to} (reason={reason})");
    }

    private void TransitionTo(TransportState newState, string reason, Exception? ex = null)
    {
        var previous = transportState;
        if (!IsTransportTransitionAllowed(previous, newState))
        {
            ThrowInvalidTransportTransition(previous, newState, reason);

            LocalOperationalLog.Error(
                "Session",
                $"event=transport_state_transition_blocked; from={previous}; to={newState}; reason={reason}; ex={ex?.GetType().Name ?? "(none)"}; attempt={connectAttempt}; transport={GetCurrentTransportKind()}; run_id={GetRunIdForLog()}; session_id={GetSessionIdForLog()}; scenario={GetScenarioForLog()}");
            return;
        }

        HandleTimingBeforeStateChange(previous, newState, reason, ex);
        transportState = newState;
        transportStateEntryTimestamps[newState] = Stopwatch.GetTimestamp();
        HandleTimingAfterStateChange(newState);
        UpdateWatchdogForState(newState, reason);
        LocalOperationalLog.Info(
            "Session",
            $"event=transport_state_changed; from={previous}; to={newState}; reason={reason}; ex={ex?.GetType().Name ?? "(none)"}; attempt={connectAttempt}; transport={GetCurrentTransportKind()}; bridge_reuse_mode={GetBridgeReuseModeForLog()}; run_id={GetRunIdForLog()}; session_id={GetSessionIdForLog()}; scenario={GetScenarioForLog()}");
        telemetrySink.OnStateChanged(new TransportStateChangedTelemetryEvent(
            previous,
            newState,
            reason,
            GetRunIdForLog(),
            GetScenarioForTelemetry(),
            GetBridgeReuseModeForTelemetry(),
            connectAttempt,
            GetCurrentTransportKind(),
            GetSessionIdForLog()));
    }

    internal bool TryTransitionTransportStateForTests(TransportState newState, string reason)
    {
        if (!IsTransportTransitionAllowed(transportState, newState))
        {
            return false;
        }

        TransitionTo(newState, reason);
        return true;
    }

    private void HandleTimingAfterStateChange(TransportState newState)
    {
        switch (newState)
        {
            case TransportState.TransportInitializing:
                transportInitTiming = TimingSpan.StartNew();
                if (!connectTiming.IsStarted)
                {
                    connectTiming = TimingSpan.StartNew();
                }
                break;
            case TransportState.BridgeStarting:
                bridgeStartTiming = TimingSpan.StartNew();
                break;
            case TransportState.Handshake:
                handshakeTiming = TimingSpan.StartNew();
                break;
            case TransportState.Reconnecting:
                reconnectTiming = TimingSpan.StartNew();
                break;
        }
    }

    private void UpdateWatchdogForState(TransportState newState, string reason)
    {
        CancelWatchdog();

        if (!watchdogOptions.Enabled)
        {
            return;
        }

        var timeout = GetWatchdogTimeout(newState);
        if (timeout is null || timeout.Value <= TimeSpan.Zero)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        var generation = Interlocked.Increment(ref watchdogGeneration);
        lock (watchdogGate)
        {
            watchdogCts = cts;
        }

        var attempt = connectAttempt;
        var sessionIdSnapshot = GetSessionIdForLog();
        LocalOperationalLog.Info(
            "Session",
            $"event=transport_watchdog_started; state={newState}; timeout_ms={timeout.Value.TotalMilliseconds:F0}; reason={reason}; attempt={attempt}; transport={GetCurrentTransportKind()}; run_id={GetRunIdForLog()}; session_id={sessionIdSnapshot}; scenario={GetScenarioForLog()}");

        _ = Task.Run(async () =>
        {
            try
            {
                await watchdogDelayAsync(timeout.Value, cts.Token).ConfigureAwait(false);
                await HandleWatchdogTimeoutAsync(newState, generation, attempt, timeout.Value).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on normal transitions/reset/dispose.
            }
            catch (Exception watchdogEx)
            {
                LocalOperationalLog.Error(
                    "Session",
                    $"event=transport_watchdog_internal_error; state={newState}; ex={watchdogEx.GetType().Name}; attempt={attempt}; transport={GetCurrentTransportKind()}; run_id={GetRunIdForLog()}; session_id={sessionIdSnapshot}; scenario={GetScenarioForLog()}");
            }
        });
    }

    private TimeSpan? GetWatchdogTimeout(TransportState state)
    {
        return state switch
        {
            TransportState.BridgeStarting => watchdogOptions.BridgeStartingTimeout,
            TransportState.Connecting => watchdogOptions.ConnectingTimeout,
            TransportState.Handshake => watchdogOptions.HandshakeTimeout,
            TransportState.Reconnecting => watchdogOptions.ReconnectingTimeout,
            _ => null
        };
    }

    private void CancelWatchdog()
    {
        CancellationTokenSource? toCancel = null;
        lock (watchdogGate)
        {
            if (watchdogCts is not null)
            {
                toCancel = watchdogCts;
                watchdogCts = null;
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
        finally
        {
            toCancel.Dispose();
        }
    }

    private void StartCachedBridgeIdleTimeout()
    {
        CancelCachedBridgeIdleTimeout();

        if (!bridgeReusePolicy.IsKeepAlive || bridgeReusePolicy.KeepAliveIdleTimeout <= TimeSpan.Zero || cachedBridgeTransport is null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        var generation = Interlocked.Increment(ref cachedBridgeIdleGeneration);
        cachedBridgeIdleCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await bridgeIdleDelayAsync(bridgeReusePolicy.KeepAliveIdleTimeout, cts.Token).ConfigureAwait(false);
                await HandleCachedBridgeIdleTimeoutAsync(generation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
            catch (Exception ex)
            {
                LocalOperationalLog.Error(
                    "Session",
                    $"event=bridge_idle_timeout_internal_error; ex={ex.GetType().Name}; attempt={connectAttempt}; transport={GetCurrentTransportKind()}; bridge_reuse_mode={GetBridgeReuseModeForLog()}; run_id={GetRunIdForLog()}; session_id={GetSessionIdForLog()}; scenario={GetScenarioForLog()}");
            }
        });
    }

    private void CancelCachedBridgeIdleTimeout()
    {
        var cts = cachedBridgeIdleCts;
        cachedBridgeIdleCts = null;
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
            // best-effort
        }
        finally
        {
            cts.Dispose();
        }
    }

    private async Task HandleCachedBridgeIdleTimeoutAsync(long generation)
    {
        ISignalingTransport? toDispose = null;

        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed || resetInProgress)
            {
                return;
            }

            if (generation != Volatile.Read(ref cachedBridgeIdleGeneration))
            {
                return;
            }

            if (transport is not null)
            {
                return; // active session resumed
            }

            toDispose = cachedBridgeTransport;
            cachedBridgeTransport = null;
            cachedBridgeIdleCts?.Dispose();
            cachedBridgeIdleCts = null;

            if (toDispose is null)
            {
                return;
            }

            LocalOperationalLog.Info(
                "Session",
                $"event=bridge_killed; reason=idle_timeout; attempt={connectAttempt}; transport=NKN; bridge_reuse_mode={GetBridgeReuseModeForLog()}; run_id={GetRunIdForLog()}; session_id={GetSessionIdForLog()}; scenario={GetScenarioForLog()}");
            telemetrySink.OnBridgeLifecycle(new BridgeLifecycleTelemetryEvent(
                EventName: "bridge_exited",
                StartMode: string.Empty,
                Pid: null,
                ReadyTimeMs: null,
                PingRttMs: null,
                UptimeMs: null,
                ExitCode: null,
                ExitReason: "killed",
                RunId: GetRunIdForLog(),
                Scenario: GetScenarioForTelemetry(),
                BridgeReuseMode: GetBridgeReuseModeForTelemetry(),
                Attempt: connectAttempt,
                Transport: "NKN",
                SessionId: GetSessionIdForLog()));
        }
        finally
        {
            lifecycleGate.Release();
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

    private async Task HandleWatchdogTimeoutAsync(
        TransportState expectedState,
        long generation,
        long expectedAttempt,
        TimeSpan timeout)
    {
        bool shouldAutoRetry = false;
        SessionRuntimeRole retryRole = SessionRuntimeRole.None;
        SessionCode? retryCode = null;

        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed || resetInProgress)
            {
                return;
            }

            if (generation != Volatile.Read(ref watchdogGeneration))
            {
                return;
            }

            if (transportState != expectedState || connectAttempt != expectedAttempt)
            {
                return;
            }

            var failure = CreateWatchdogFailure(expectedState, timeout);
            LocalOperationalLog.Error(
                "Session",
                $"event=transport_watchdog_timeout; state={expectedState}; timeout_ms={timeout.TotalMilliseconds:F0}; attempt={connectAttempt}; transport={GetCurrentTransportKind()}; run_id={GetRunIdForLog()}; session_id={GetSessionIdForLog()}; scenario={GetScenarioForLog()}");

            pendingJoinRequest = null;
            TransitionTo(TransportState.Failed, "watchdog_timeout");
            SetState(SessionRuntimeState.Failed, GetWatchdogUserMessage(expectedState));
            LogTransportFailure(failure, "watchdog_timeout");

            if (watchdogOptions.AutoRetryEnabled && role != SessionRuntimeRole.None && currentCode is not null)
            {
                shouldAutoRetry = true;
                retryRole = role;
                retryCode = currentCode;
            }
        }
        finally
        {
            lifecycleGate.Release();
        }

        if (shouldAutoRetry && retryCode is not null)
        {
            try
            {
                LocalOperationalLog.Info(
                    "Session",
                    $"event=transport_watchdog_retry_requested; attempt={connectAttempt}; transport={GetCurrentTransportKind()}; run_id={GetRunIdForLog()}; session_id={GetSessionIdForLog()}; scenario={GetScenarioForLog()}");
                await ResetAsync(notifyRemoteSessionEnd: false).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort; leave Failed state if reset fails.
            }
        }
    }

    private TransportFailure CreateWatchdogFailure(TransportState timedOutState, TimeSpan timeout)
    {
        var timeoutMs = timeout.TotalMilliseconds.ToString("F0");
        return timedOutState switch
        {
            TransportState.BridgeStarting => TransportFailure.Create(
                TransportFailureCategory.BridgeStartFailure,
                $"Bridge start timed out after {timeoutMs} ms.",
                rawError: "bridge_start_timeout",
                isTransient: true),
            TransportState.Connecting => TransportFailure.Create(
                TransportFailureCategory.PeerUnreachable,
                $"Connect timed out after {timeoutMs} ms.",
                rawError: "connect_timeout",
                isTransient: true),
            TransportState.Handshake => TransportFailure.Create(
                TransportFailureCategory.HandshakeTimeout,
                $"Handshake timed out after {timeoutMs} ms.",
                rawError: "handshake_timeout",
                isTransient: true),
            TransportState.Reconnecting => TransportFailure.Create(
                TransportFailureCategory.Unknown,
                $"Reconnect timed out after {timeoutMs} ms.",
                rawError: "reconnect_timeout",
                isTransient: true),
            _ => TransportFailure.Create(
                TransportFailureCategory.Unknown,
                $"State watchdog timed out after {timeoutMs} ms.",
                rawError: "watchdog_timeout",
                isTransient: true),
        };
    }

    private string GetWatchdogUserMessage(TransportState timedOutState)
    {
        return timedOutState switch
        {
            TransportState.BridgeStarting => "Please reinstall.",
            TransportState.Handshake => "No response yet.",
            TransportState.Connecting when role == SessionRuntimeRole.Helper => "No one found with that code.",
            _ => "Connection lost.",
        };
    }

    private void BeginConnectAttempt(SessionRuntimeRole nextRole, SessionCode code)
    {
        var key = $"{nextRole}:{code.Digits}";
        if (!string.Equals(attemptSessionKey, key, StringComparison.Ordinal))
        {
            attemptSessionKey = key;
            connectAttempt = 1;
            sessionId = Guid.NewGuid().ToString("N")[..8];
        }
        else
        {
            connectAttempt++;
        }

        lastTransportFailure = null;
        bridgeStartTiming = default;
        transportInitTiming = default;
        connectTiming = default;
        handshakeTiming = default;
        reconnectTiming = default;
    }

    private void HandleTimingBeforeStateChange(
        TransportState previous,
        TransportState next,
        string reason,
        Exception? ex)
    {
        if (previous == TransportState.TransportInitializing &&
            next != TransportState.TransportInitializing &&
            transportInitTiming.IsStarted)
        {
            CompleteDurationMetric("transport_init_duration_ms", "transport_init_completed", transportInitTiming, reason, ex, next == TransportState.Failed);
            transportInitTiming = default;
        }

        if (previous == TransportState.BridgeStarting &&
            next != TransportState.BridgeStarting &&
            bridgeStartTiming.IsStarted)
        {
            CompleteDurationMetric("bridge_start_duration_ms", "bridge_start_completed", bridgeStartTiming, reason, ex, next == TransportState.Failed);
            bridgeStartTiming = default;
        }

        if (previous == TransportState.Handshake &&
            next != TransportState.Handshake &&
            handshakeTiming.IsStarted)
        {
            CompleteDurationMetric("handshake_duration_ms", "handshake_completed", handshakeTiming, reason, ex, next == TransportState.Failed);
            handshakeTiming = default;
        }

        if (previous == TransportState.Reconnecting &&
            next != TransportState.Reconnecting &&
            reconnectTiming.IsStarted)
        {
            CompleteDurationMetric("reconnect_duration_ms", "reconnect_completed", reconnectTiming, reason, ex, next == TransportState.Failed);
            reconnectTiming = default;
        }

        var connectCompletes =
            connectTiming.IsStarted &&
            (
                next == TransportState.Connected ||
                next == TransportState.Failed ||
                next == TransportState.Disposed
            );

        if (connectCompletes)
        {
            CompleteDurationMetric("connect_duration_ms", "connect_completed", connectTiming, reason, ex, next != TransportState.Connected);
            connectTiming = default;
        }
    }

    private void CompleteDurationMetric(
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

        lastDurationMetricsMs[metricName] = durationMs;
        var transportKind = GetCurrentTransportKind();
        var sessionIdForLog = GetSessionIdForLog();
        LocalOperationalLog.Info(
            "Session",
            $"event={eventName}; duration_ms={durationMs:F2}; attempt={connectAttempt}; transport={transportKind}; bridge_reuse_mode={GetBridgeReuseModeForLog()}; run_id={GetRunIdForLog()}; session_id={sessionIdForLog}; scenario={GetScenarioForLog()}; outcome={(failed ? "failed" : "success")}; reason={reason}; ex={ex?.GetType().Name ?? "(none)"}");
        telemetrySink.OnTimingCompleted(new TransportTimingCompletedTelemetryEvent(
            eventName,
            metricName,
            durationMs,
            failed,
            reason,
            GetRunIdForLog(),
            GetScenarioForTelemetry(),
            GetBridgeReuseModeForTelemetry(),
            connectAttempt,
            transportKind,
            sessionIdForLog));
    }

    private string GetCurrentTransportKind()
    {
        return transport switch
        {
            null => "(none)",
            NknSignalingTransport => "NKN",
            _ => "DevLocal"
        };
    }

    private string GetRunIdForLog() => TransportTelemetryContext.RunId;

    private string GetScenarioForTelemetry() => TransportTelemetryContext.GetScenarioLabel();

    private string GetScenarioForLog()
    {
        var scenario = GetScenarioForTelemetry();
        return string.IsNullOrWhiteSpace(scenario) ? "(none)" : scenario;
    }

    private string GetBridgeReuseModeForTelemetry() => bridgeReusePolicy.Label;

    private string GetBridgeReuseModeForLog() => bridgeReusePolicy.Label;

    private string GetSessionIdForLog()
    {
        return string.IsNullOrWhiteSpace(sessionId) ? "(none)" : sessionId;
    }

    private void LogTransportFailure(TransportFailure failure, string reason)
    {
        lastTransportFailure = failure;
        var duration = GetLastDurationMetricMilliseconds("connect_duration_ms");
        var durationField = duration.HasValue ? duration.Value.ToString("F2") : "(none)";
        var transportKind = GetCurrentTransportKind();
        var sessionIdForLog = GetSessionIdForLog();

        LocalOperationalLog.Error(
            "Session",
            $"event=transport_failed; category={failure.Category}; is_transient={failure.IsTransient}; message={failure.Message}; exception_type={failure.ExceptionType}; attempt={connectAttempt}; transport={transportKind}; bridge_reuse_mode={GetBridgeReuseModeForLog()}; state={transportState}; duration_ms={durationField}; run_id={GetRunIdForLog()}; session_id={sessionIdForLog}; scenario={GetScenarioForLog()}; correlation_id={failure.CorrelationId}; reason={reason}; raw={failure.RawError}");
        telemetrySink.OnFailure(new TransportFailureTelemetryEvent(
            failure.Category,
            failure.IsTransient,
            failure.Message,
            failure.ExceptionType,
            GetRunIdForLog(),
            GetScenarioForTelemetry(),
            GetBridgeReuseModeForTelemetry(),
            connectAttempt,
            transportKind,
            transportState.ToString(),
            duration,
            sessionIdForLog));
    }

    private void SetState(SessionRuntimeState nextState, string nextStatusText)
    {
        state = nextState;
        statusText = nextStatusText;
        LocalOperationalLog.Info(
            "Session",
            $"state={state}; role={role}; status={SanitizeStatusForLog(statusText)}");

        StateChanged?.Invoke(
            this,
            new SessionRuntimeStateChangedEventArgs(state, role, statusText, currentCode));
    }

    private void ThrowIfStartInProgress()
    {
        if (startInProgress)
        {
            throw new InvalidOperationException("A session start is already in progress.");
        }
    }

    private static bool ShouldNotifyRemoteSessionEnd(
        ISignalingTransport? oldTransport,
        SessionRuntimeRole oldRole,
        SessionRuntimeState oldState)
    {
        if (oldRole == SessionRuntimeRole.None)
        {
            return false;
        }

        if (oldTransport is not NknSignalingTransport nknTransport || !nknTransport.CanSendSessionEnd)
        {
            return false;
        }

        return oldState is SessionRuntimeState.Waiting
            or SessionRuntimeState.IncomingJoinRequest
            or SessionRuntimeState.Connecting
            or SessionRuntimeState.Connected;
    }

    private static async Task TrySendRemoteSessionEndAsync(
        ISignalingTransport? oldTransport,
        SessionRuntimeRole oldRole,
        SessionRuntimeState oldState)
    {
        if (!ShouldNotifyRemoteSessionEnd(oldTransport, oldRole, oldState))
        {
            return;
        }

        var nknTransport = (NknSignalingTransport)oldTransport!;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await nknTransport.SendSessionEndAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort only.
        }
    }

    private static string SanitizeStatusForLog(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(none)";
        }

        var trimmed = text.Trim();
        return trimmed.Length <= 120 ? trimmed : trimmed[..120];
    }
}
