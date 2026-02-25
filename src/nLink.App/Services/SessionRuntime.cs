using System;
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

public sealed class SessionRuntime : IDisposable
{
    private readonly Func<ISignalingTransport> createTransport;
    private readonly SessionChatService chatService = new();
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);

    private CancellationTokenSource? sessionCts;
    private ISignalingTransport? transport;
    private IncomingJoinRequestEventArgs? pendingJoinRequest;
    private SessionRuntimeRole role;
    private SessionRuntimeState state = SessionRuntimeState.Idle;
    private SessionCode? currentCode;
    private string statusText = string.Empty;
    private bool resetInProgress;
    private bool startInProgress;
    private bool remoteSessionEndHandling;
    private bool disposed;

    public SessionRuntime(Func<ISignalingTransport> createTransport)
    {
        this.createTransport = createTransport ?? throw new ArgumentNullException(nameof(createTransport));

        chatService.MessageReceived += OnChatMessageReceived;
        chatService.MessageReceivedBeforeApproved += OnChatMessageReceivedBeforeApproved;
        chatService.StateChanged += OnChatStateChanged;
    }

    public event EventHandler<SessionRuntimeStateChangedEventArgs>? StateChanged;

    public event EventHandler? IncomingJoinRequestAvailable;

    public event EventHandler? Approved;

    public event EventHandler? Rejected;

    public event EventHandler? Disconnected;

    public event EventHandler<ChatMessageEventArgs>? ChatMessageReceived;

    public event EventHandler? ChatMessageReceivedBeforeApproved;

    public event EventHandler? ChatStateChanged;

    public SessionRuntimeState State => state;

    public SessionRuntimeRole Role => role;

    public string StatusText => statusText;

    public SessionCode? CurrentCode => currentCode;

    public bool HasPendingJoinRequest => pendingJoinRequest is not null;

    public bool CanSendChat => chatService.CanSend;

    public bool HasSessionKey => chatService.HasSessionKey;

    public bool IsApproved => chatService.IsApproved;

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

            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(uiCt);
            var nextTransport = createTransport();
            sessionCts = linkedCts;
            transport = nextTransport;
            role = SessionRuntimeRole.Helpee;
            currentCode = code;
            pendingJoinRequest = null;
            SessionTimeline.Record("Started");
            SessionTimeline.Record("Hosting");

            WireTransport(nextTransport);
            chatService.AttachTransport(nextTransport);

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

            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(uiCt);
            var nextTransport = createTransport();
            sessionCts = linkedCts;
            transport = nextTransport;
            role = SessionRuntimeRole.Helper;
            currentCode = code;
            pendingJoinRequest = null;
            SessionTimeline.Record("Started");
            SessionTimeline.Record("Joining");

            WireTransport(nextTransport);
            chatService.AttachTransport(nextTransport);

            SetState(SessionRuntimeState.Connecting, "Connecting…");

            await nextTransport.JoinAsync(code, linkedCts.Token).ConfigureAwait(false);
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
            SetState(SessionRuntimeState.Failed, userStatusText ?? string.Empty);
        }
        finally
        {
            lifecycleGate.Release();
        }
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

        disposed = true;

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
            SetState(SessionRuntimeState.Disconnected, message);
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

            sessionCts = null;
            transport = null;
            pendingJoinRequest = null;
            role = SessionRuntimeRole.None;
            currentCode = null;

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
                try
                {
                    await Task.Run(oldTransport.Dispose).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort cleanup.
                }
            }

            SetState(SessionRuntimeState.Idle, string.Empty);
        }
        finally
        {
            resetInProgress = false;
        }
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
        SetState(SessionRuntimeState.IncomingJoinRequest, "Helper on this PC wants to connect. Click Allow.");
        IncomingJoinRequestAvailable?.Invoke(this, EventArgs.Empty);
    }

    private void OnTransportApproved(object? sender, EventArgs e)
    {
        SessionTimeline.Record("Approved");
        SetState(SessionRuntimeState.Connected, "Connected");
        Approved?.Invoke(this, EventArgs.Empty);
    }

    private void OnTransportRejected(object? sender, EventArgs e)
    {
        pendingJoinRequest = null;
        SessionTimeline.Record("Rejected");
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
            SetState(SessionRuntimeState.Failed, "Connection lost.");
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
