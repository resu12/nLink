using System;
using System.Text.RegularExpressions;
using System.Threading;

namespace NLink.App.Services;

internal interface IStatusPresenterSource
{
    event EventHandler<SessionRuntimeStateChangedEventArgs>? StateChanged;
    event EventHandler<SessionRuntimeTransientStatusChangedEventArgs>? TransientStatusChanged;

    SessionRuntimeState State { get; }
    TransportState TransportLifecycleState { get; }
    string StatusText { get; }
    TransportFailure? LastTransportFailure { get; }
    DiagnosticsSnapshot GetDiagnosticsSnapshot();
}

internal sealed class SessionRuntimeStatusPresenterSource : IStatusPresenterSource
{
    private readonly SessionRuntime runtime;

    public SessionRuntimeStatusPresenterSource(SessionRuntime runtime)
    {
        this.runtime = runtime;
        runtime.StateChanged += ForwardStateChanged;
        runtime.TransientStatusChanged += ForwardTransientStatusChanged;
    }

    public event EventHandler<SessionRuntimeStateChangedEventArgs>? StateChanged;
    public event EventHandler<SessionRuntimeTransientStatusChangedEventArgs>? TransientStatusChanged;

    public SessionRuntimeState State => runtime.State;
    public TransportState TransportLifecycleState => runtime.TransportLifecycleState;
    public string StatusText => runtime.StatusText;
    public TransportFailure? LastTransportFailure => runtime.LastTransportFailure;
    public DiagnosticsSnapshot GetDiagnosticsSnapshot() => runtime.GetDiagnosticsSnapshot();

    private void ForwardStateChanged(object? sender, SessionRuntimeStateChangedEventArgs e) => StateChanged?.Invoke(this, e);

    private void ForwardTransientStatusChanged(object? sender, SessionRuntimeTransientStatusChangedEventArgs e) => TransientStatusChanged?.Invoke(this, e);
}

public sealed class UserFacingStatusChangedEventArgs : EventArgs
{
    public UserFacingStatusChangedEventArgs(UserFacingStatus status)
    {
        Status = status;
    }

    public UserFacingStatus Status { get; }
}

public sealed class StatusPresenter : IDisposable
{
    private static readonly Regex AttemptRegex = new(@"\battempt\s+(?<n>\d+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RetrySecondsRegex = new(@"\bnext retry in\s+(?<s>\d+)s\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly TimeSpan DefaultFailureDedupeWindow = TimeSpan.FromSeconds(10);

    private readonly IStatusPresenterSource source;
    private readonly ITimer countdownTimer;
    private readonly bool ownsCountdownTimer;
    private readonly object gate = new();
    private readonly Func<DateTimeOffset> nowProvider;
    private readonly TimeSpan failureDedupeWindow;
    private int countdownGeneration;
    private bool disposed;
    private TransportFailureCategory? lastEmittedFailureCategory;
    private DateTimeOffset lastEmittedFailureAtUtc;

    public StatusPresenter(SessionRuntime runtime)
        : this(new SessionRuntimeStatusPresenterSource(runtime), null)
    {
    }

    internal StatusPresenter(IStatusPresenterSource source)
        : this(source, null, null, null)
    {
    }

    internal StatusPresenter(IStatusPresenterSource source, ITimer? countdownTimer)
        : this(source, countdownTimer, null, null)
    {
    }

    internal StatusPresenter(
        IStatusPresenterSource source,
        ITimer? countdownTimer,
        Func<DateTimeOffset>? nowProvider,
        TimeSpan? failureDedupeWindow)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.countdownTimer = countdownTimer ?? new ThreadPoolTimerAdapter();
        ownsCountdownTimer = countdownTimer is null;
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
        this.failureDedupeWindow = failureDedupeWindow ?? DefaultFailureDedupeWindow;
        CurrentStatus = UserFacingStatus.IdleStatus;
        source.StateChanged += OnStateChanged;
        source.TransientStatusChanged += OnTransientStatusChanged;

        // Initialize from current runtime state.
        SafeRefreshFromState();
    }

    public event EventHandler<UserFacingStatusChangedEventArgs>? StatusChanged;

    public UserFacingStatus CurrentStatus { get; private set; }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        StopCountdown();
        source.StateChanged -= OnStateChanged;
        source.TransientStatusChanged -= OnTransientStatusChanged;
        if (ownsCountdownTimer)
        {
            this.countdownTimer.Dispose();
        }
    }

    private void OnStateChanged(object? sender, SessionRuntimeStateChangedEventArgs e)
    {
        if (disposed)
        {
            return;
        }

        SafeRefreshFromState();
    }

    private void OnTransientStatusChanged(object? sender, SessionRuntimeTransientStatusChangedEventArgs e)
    {
        if (disposed)
        {
            return;
        }

        try
        {
            if (!e.IsVisible)
            {
                SafeRefreshFromState();
                return;
            }

            var kind = GetTransientKind(e.Text);
            var severity = kind == UserStatusKind.Reconnecting ? FailureSeverity.Warning : FailureSeverity.Info;
            var title = kind switch
            {
                UserStatusKind.Reconnecting => "Reconnecting",
                UserStatusKind.Handshake => "Finalizing connection",
                _ => "Connecting"
            };

            var attempt = TryParseInt(AttemptRegex, e.Text, "n");
            var nextRetrySeconds = TryParseInt(RetrySecondsRegex, e.Text, "s");
            var diag = source.GetDiagnosticsSnapshot();
            SetStatus(new UserFacingStatus(
                Kind: kind,
                Title: title,
                Message: e.Text ?? string.Empty,
                Severity: severity,
                Attempt: attempt,
                NextRetryInSeconds: nextRetrySeconds,
                CanCancel: e.CanCancel,
                CanCopyDiagnostics: false,
                CorrelationId: null));
        }
        catch
        {
            // Never throw from presenter; fallback to a degraded banner.
            SetStatus(new UserFacingStatus(
                UserStatusKind.Degraded,
                "Status unavailable",
                "The status display could not be updated.",
                FailureSeverity.Warning,
                CanCopyDiagnostics: true));
        }
    }

    private void SafeRefreshFromState()
    {
        try
        {
            RefreshFromState();
        }
        catch
        {
            SetStatus(new UserFacingStatus(
                UserStatusKind.Degraded,
                "Status unavailable",
                "The status display could not be updated.",
                FailureSeverity.Warning,
                CanCopyDiagnostics: true));
        }
    }

    private void RefreshFromState()
    {
        var failure = source.LastTransportFailure;
        var diag = source.GetDiagnosticsSnapshot();

        if (failure?.Category == TransportFailureCategory.UserCancelled || source.State == SessionRuntimeState.Idle)
        {
            SetStatus(UserFacingStatus.IdleStatus);
            return;
        }

        if (source.TransportLifecycleState == TransportState.Connected || source.State == SessionRuntimeState.Connected)
        {
            SetStatus(UserFacingStatus.ConnectedStatus(message: "Connected"));
            return;
        }

        if (source.TransportLifecycleState == TransportState.Reconnecting)
        {
            SetStatus(new UserFacingStatus(
                UserStatusKind.Reconnecting,
                "Reconnecting",
                string.IsNullOrWhiteSpace(source.StatusText) ? "Reconnecting…" : source.StatusText,
                FailureSeverity.Warning,
                Attempt: diag.AttemptNumber > 0 ? (int)diag.AttemptNumber : null,
                CanCancel: true,
                CanCopyDiagnostics: false));
            return;
        }

        if (source.TransportLifecycleState is TransportState.BridgeStarting or TransportState.BridgeReady or TransportState.TransportInitializing or TransportState.Connecting)
        {
            SetStatus(new UserFacingStatus(
                UserStatusKind.Connecting,
                "Connecting",
                string.IsNullOrWhiteSpace(source.StatusText) ? "Connecting…" : source.StatusText,
                FailureSeverity.Info,
                Attempt: diag.AttemptNumber > 0 ? (int)diag.AttemptNumber : null,
                CanCancel: true));
            return;
        }

        if (source.TransportLifecycleState == TransportState.Handshake || source.State == SessionRuntimeState.IncomingJoinRequest)
        {
            var isAwaitingApproval = source.State == SessionRuntimeState.IncomingJoinRequest;
            SetStatus(new UserFacingStatus(
                UserStatusKind.Handshake,
                isAwaitingApproval ? "Waiting for approval" : "Finalizing connection",
                string.IsNullOrWhiteSpace(source.StatusText)
                    ? isAwaitingApproval ? "Waiting for approval…" : "Finalizing connection…"
                    : source.StatusText,
                FailureSeverity.Info,
                Attempt: diag.AttemptNumber > 0 ? (int)diag.AttemptNumber : null,
                CanCancel: true));
            return;
        }

        if (source.TransportLifecycleState == TransportState.Failed || source.State is SessionRuntimeState.Failed or SessionRuntimeState.Disconnected or SessionRuntimeState.Rejected)
        {
            var runtimeMessage = source.StatusText?.Trim() ?? string.Empty;
            if (failure is null && !string.IsNullOrWhiteSpace(runtimeMessage))
            {
                var title = runtimeMessage.Contains("ended the session", StringComparison.OrdinalIgnoreCase)
                    ? "Session ended"
                    : "Connection issue";
                SetStatus(new UserFacingStatus(
                    UserStatusKind.Failed,
                    title,
                    runtimeMessage,
                    FailureSeverity.Error,
                    Attempt: diag.AttemptNumber > 0 ? (int)diag.AttemptNumber : null,
                    CanCancel: false,
                    CanCopyDiagnostics: true,
                    CorrelationId: null));
                return;
            }

            var category = failure?.Category ?? TransportFailureCategory.Unknown;
            var copy = FailureCopyMap.For(category);
            SetStatus(new UserFacingStatus(
                UserStatusKind.Failed,
                copy.Title,
                copy.Message,
                FailureSeverity.Error,
                Attempt: diag.AttemptNumber > 0 ? (int)diag.AttemptNumber : null,
                CanCancel: false,
                CanCopyDiagnostics: true,
                CorrelationId: failure?.CorrelationId),
                failureCategoryForDedupe: category);
            return;
        }

        if (source.TransportLifecycleState == TransportState.Disposed)
        {
            SetStatus(UserFacingStatus.IdleStatus);
            return;
        }

        SetStatus(UserFacingStatus.IdleStatus);
    }

    private static UserStatusKind GetTransientKind(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return UserStatusKind.Connecting;
        }

        if (text.Contains("Reconnecting", StringComparison.OrdinalIgnoreCase))
        {
            return UserStatusKind.Reconnecting;
        }

        if (text.Contains("Handshake", StringComparison.OrdinalIgnoreCase))
        {
            return UserStatusKind.Handshake;
        }

        return UserStatusKind.Connecting;
    }

    private static int? TryParseInt(Regex regex, string? input, string groupName)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var match = regex.Match(input);
        if (!match.Success)
        {
            return null;
        }

        if (int.TryParse(match.Groups[groupName].Value, out var value))
        {
            return value;
        }

        return null;
    }

    private void SetStatus(UserFacingStatus status, TransportFailureCategory? failureCategoryForDedupe = null)
    {
        if (disposed)
        {
            return;
        }

        ConfigureCountdown(status);

        if (ShouldSuppressDuplicateFailure(status, failureCategoryForDedupe))
        {
            return;
        }

        if (Equals(CurrentStatus, status))
        {
            return;
        }

        CurrentStatus = status;
        if (failureCategoryForDedupe.HasValue && status.Kind == UserStatusKind.Failed)
        {
            lastEmittedFailureCategory = failureCategoryForDedupe.Value;
            lastEmittedFailureAtUtc = nowProvider();
        }
        StatusChanged?.Invoke(this, new UserFacingStatusChangedEventArgs(status));
    }

    private bool ShouldSuppressDuplicateFailure(UserFacingStatus status, TransportFailureCategory? failureCategory)
    {
        if (status.Kind != UserStatusKind.Failed || !failureCategory.HasValue)
        {
            return false;
        }

        if (CurrentStatus.Kind != UserStatusKind.Failed)
        {
            return false;
        }

        if (lastEmittedFailureCategory != failureCategory.Value)
        {
            return false;
        }

        var elapsed = nowProvider() - lastEmittedFailureAtUtc;
        return elapsed >= TimeSpan.Zero && elapsed <= failureDedupeWindow;
    }

    private void ConfigureCountdown(UserFacingStatus status)
    {
        if (status.Kind == UserStatusKind.Reconnecting && status.NextRetryInSeconds is > 0)
        {
            var generation = Interlocked.Increment(ref countdownGeneration);
            countdownTimer.Start(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), () => OnCountdownTick(generation));
            return;
        }

        StopCountdown();
    }

    private void StopCountdown()
    {
        Interlocked.Increment(ref countdownGeneration);
        countdownTimer.Stop();
    }

    private void OnCountdownTick(int generation)
    {
        if (disposed || generation != Volatile.Read(ref countdownGeneration))
        {
            return;
        }

        UserFacingStatus? nextStatus = null;
        lock (gate)
        {
            if (disposed || generation != Volatile.Read(ref countdownGeneration))
            {
                return;
            }

            if (CurrentStatus.Kind != UserStatusKind.Reconnecting || CurrentStatus.NextRetryInSeconds is not int seconds)
            {
                return;
            }

            var nextSeconds = Math.Max(0, seconds - 1);
            if (nextSeconds == seconds)
            {
                return;
            }

            nextStatus = CurrentStatus with { NextRetryInSeconds = nextSeconds };
            if (nextSeconds == 0)
            {
                // Let state/transient events update text from here; stop ticking.
                Interlocked.Increment(ref countdownGeneration);
                countdownTimer.Stop();
            }
        }

        if (nextStatus is not null && !Equals(CurrentStatus, nextStatus))
        {
            CurrentStatus = nextStatus;
            StatusChanged?.Invoke(this, new UserFacingStatusChangedEventArgs(nextStatus));
        }
    }
}

