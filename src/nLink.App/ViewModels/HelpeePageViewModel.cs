using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using NLink.App.Configuration;
using NLink.App.Services;
using NLink.App.Threading;
using NLink.Core;
using NLink.Core.Chat;
using NLink.Infra.Nkn;

namespace NLink.App.ViewModels;

public sealed class HelpeePageViewModel : ViewModelBase, IDisposable, IChatPanelBindings
{
    private static readonly TimeSpan DefaultIncomingRequestTimeout = TimeSpan.FromSeconds(20);
    private readonly Action cancelAction;
    private readonly Action? openDiagnosticsAction;
    private readonly TransportRuntimeConfig transportConfig;
    private readonly SessionRuntime sessionRuntime;
    private readonly IClipboardService? clipboardService;
    private SessionCode sessionCode = SessionCode.CreateRandom();
    private bool hasIncomingRequest;
    private bool isRequestAllowed;
    private bool showTroubleshooting;
    private bool showChatNotice;
    private string codeCopyStatusText = "Tell this code to your helper.";
    private string connectionStatus = "Waiting for helper…";
    private string connectionState = "Waiting";
    private string chatDraft = string.Empty;
    private bool simulatedIncomingRequest;
    private SessionReliabilityAttempt? reliabilityAttempt;
    private CancellationTokenSource? codeCopyStatusResetCts;
    private CancellationTokenSource? incomingRequestTimeoutCts;
    private readonly TimeSpan incomingRequestTimeout;
    private bool startupBlocked;
    private bool disposed;

    public HelpeePageViewModel(
        Action cancelAction,
        TransportRuntimeConfig transportConfig,
        SessionRuntime sessionRuntime,
        Action? openDiagnosticsAction = null,
        IClipboardService? clipboardService = null,
        ShareMessageConfig? shareMessageConfig = null,
        TimeSpan? incomingRequestTimeout = null)
    {
        this.cancelAction = cancelAction;
        this.openDiagnosticsAction = openDiagnosticsAction;
        this.transportConfig = transportConfig;
        this.sessionRuntime = sessionRuntime;
        this.clipboardService = clipboardService;
        this.incomingRequestTimeout = incomingRequestTimeout ?? DefaultIncomingRequestTimeout;
        _ = shareMessageConfig;

        ChatMessages = new ObservableCollection<ChatLineViewModel>();

        sessionRuntime.StateChanged += OnSessionRuntimeStateChanged;
        sessionRuntime.IncomingJoinRequestAvailable += OnIncomingJoinRequestAvailable;
        sessionRuntime.Disconnected += OnRuntimeDisconnected;
        sessionRuntime.ChatMessageReceived += OnChatMessageReceived;
        sessionRuntime.ChatMessageReceivedBeforeApproved += OnChatMessageReceivedBeforeApproved;
        sessionRuntime.ChatStateChanged += OnChatStateChanged;

        RegenerateCodeCommand = new RelayCommand(RegenerateCode);
        CopyCodeCommand = new AsyncRelayCommand(CopyCodeAsync);
        SimulateIncomingRequestCommand = new RelayCommand(SimulateIncomingRequest);
        ToggleTroubleshootingCommand = new RelayCommand(ToggleTroubleshooting);
        AllowCommand = new RelayCommand(AllowIncomingRequest, CanAllowIncomingRequest);
        DeclineCommand = new AsyncRelayCommand(DeclineIncomingRequestAsync, CanDeclineIncomingRequest);
        SendChatCommand = new AsyncRelayCommand(SendChatAsync, CanSendChat);
        RetryCommand = new AsyncRelayCommand(RetryAsync);
        OpenDiagnosticsCommand = new RelayCommand(OpenDiagnostics, CanOpenDiagnostics);
        CancelCommand = new RelayCommand(CancelAndGoBack);

        InitializeStartupAvailabilityState();
        if (!IsStartupBlocked)
        {
            StartHosting();
        }
    }

    public string PageTitle => IsIncomingRequestView ? "Someone wants to connect" : "Your code";
    public bool ShowPageHeader => !IsConnectedView;

    public string PageSubtitle => IsIncomingRequestView ? string.Empty : CodeCopyStatusText;
    public bool ShowPageSubtitle => ConnectionState == "Waiting" && !IsStartupBlocked;

    public string ShareCode => sessionCode.Digits;

    public string IncomingHelperName => "Helper on this PC";

    public string CodeCopyStatusText
    {
        get => codeCopyStatusText;
        private set => SetProperty(ref codeCopyStatusText, value);
    }

    public bool HasIncomingRequest
    {
        get => hasIncomingRequest;
        private set
        {
            if (SetProperty(ref hasIncomingRequest, value))
            {
                AllowCommand.NotifyCanExecuteChanged();
                DeclineCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsRequestAllowed
    {
        get => isRequestAllowed;
        private set
        {
            if (SetProperty(ref isRequestAllowed, value))
            {
                AllowCommand.NotifyCanExecuteChanged();
                DeclineCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool ShowTroubleshooting
    {
        get => showTroubleshooting;
        private set => SetProperty(ref showTroubleshooting, value);
    }

    public bool ShowDevTroubleshooting => transportConfig.IsDevLocal && !IsIncomingRequestView && !IsStartupBlocked;

    public string ConnectionStatus
    {
        get => connectionStatus;
        private set => SetProperty(ref connectionStatus, value);
    }

    public string ConnectionState
    {
        get => connectionState;
        private set
        {
            if (SetProperty(ref connectionState, value))
            {
                OnPropertyChanged(nameof(IsWaitingView));
                OnPropertyChanged(nameof(IsIncomingRequestView));
                OnPropertyChanged(nameof(IsConnectedView));
                OnPropertyChanged(nameof(ShowWaitingPanel));
                OnPropertyChanged(nameof(ShowIncomingRequestPanel));
                OnPropertyChanged(nameof(ShowConnectedPanel));
                OnPropertyChanged(nameof(ShowWaitingCodeActions));
                OnPropertyChanged(nameof(ShowBackButton));
                OnPropertyChanged(nameof(PageTitle));
                OnPropertyChanged(nameof(PageSubtitle));
                OnPropertyChanged(nameof(ShowPageSubtitle));
                OnPropertyChanged(nameof(ShowPageHeader));
                OnPropertyChanged(nameof(StatusLineText));
                OnPropertyChanged(nameof(SecondaryActionText));
                OnPropertyChanged(nameof(ShowChatSection));
            }
        }
    }

    public bool IsWaitingView => ConnectionState is "Waiting" or "Disconnected" or "Failed";

    public bool IsIncomingRequestView => ConnectionState == "IncomingRequest";

    public bool IsConnectedView => ConnectionState == "Connected";

    public bool ShowChatSection => IsConnectedView;
    public bool ShowWaitingPanel => IsWaitingView && !IsStartupBlocked;
    public bool ShowIncomingRequestPanel => IsIncomingRequestView && !IsStartupBlocked;
    public bool ShowConnectedPanel => ShowChatSection && !IsStartupBlocked;

    public bool ShowBackButton => !IsConnectedView && !IsIncomingRequestView && !IsStartupBlocked;

    public string StatusLineText => IsIncomingRequestView
        ? "Waiting for you to allow."
        : IsConnectedView
            ? ConnectionStatus
            : ConnectionStatus;

    public string SecondaryActionText => IsConnectedView ? "Disconnect" : "New code";

    public bool IsStartupBlocked
    {
        get => startupBlocked;
        private set
        {
            if (SetProperty(ref startupBlocked, value))
            {
                OnPropertyChanged(nameof(ShowHostingUi));
                OnPropertyChanged(nameof(ShowWaitingPanel));
                OnPropertyChanged(nameof(ShowIncomingRequestPanel));
                OnPropertyChanged(nameof(ShowConnectedPanel));
                OnPropertyChanged(nameof(ShowWaitingCodeActions));
                OnPropertyChanged(nameof(ShowDevTroubleshooting));
                OnPropertyChanged(nameof(ShowBackButton));
                OnPropertyChanged(nameof(ShowRetryAction));
                OnPropertyChanged(nameof(ShowOpenDiagnosticsLink));
                OnPropertyChanged(nameof(ShowPageSubtitle));
                OpenDiagnosticsCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool ShowHostingUi => !IsStartupBlocked;
    public bool ShowWaitingCodeActions => ShowHostingUi && ConnectionState == "Waiting";

    public ObservableCollection<ChatLineViewModel> ChatMessages { get; }

    public string ChatPanelTitle => "Message";
    public bool HasChatMessages => ChatMessages.Count > 0;
    public bool ShowNoMessagesPlaceholder => !HasChatMessages;

    public string ChatDraft
    {
        get => chatDraft;
        set
        {
            if (SetProperty(ref chatDraft, value))
            {
                SendChatCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsChatReady => sessionRuntime.CanSendChat;

    public bool ShowChatNotice
    {
        get => showChatNotice;
        private set => SetProperty(ref showChatNotice, value);
    }

    public string ChatNoticeText => "You received a message";

    public IRelayCommand RegenerateCodeCommand { get; }

    public IAsyncRelayCommand CopyCodeCommand { get; }

    public IRelayCommand SimulateIncomingRequestCommand { get; }

    public IRelayCommand ToggleTroubleshootingCommand { get; }

    public RelayCommand AllowCommand { get; }

    public IAsyncRelayCommand DeclineCommand { get; }

    public IAsyncRelayCommand SendChatCommand { get; }

    public IAsyncRelayCommand RetryCommand { get; }

    public IRelayCommand OpenDiagnosticsCommand { get; }

    public IRelayCommand CancelCommand { get; }
    public IRelayCommand EndSessionCommand => CancelCommand;

    public bool ShowRetryAction => !IsStartupBlocked && ConnectionState == "Failed";

    public bool ShowOpenDiagnosticsLink =>
        openDiagnosticsAction is not null &&
        (IsStartupBlocked || string.Equals(ConnectionStatus, UserErrorMapper.NknStartFailedReinstall(), StringComparison.Ordinal));

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        sessionRuntime.StateChanged -= OnSessionRuntimeStateChanged;
        sessionRuntime.IncomingJoinRequestAvailable -= OnIncomingJoinRequestAvailable;
        sessionRuntime.Disconnected -= OnRuntimeDisconnected;
        sessionRuntime.ChatMessageReceived -= OnChatMessageReceived;
        sessionRuntime.ChatMessageReceivedBeforeApproved -= OnChatMessageReceivedBeforeApproved;
        sessionRuntime.ChatStateChanged -= OnChatStateChanged;
        sessionRuntime.SetReliabilityAttempt(null);
        codeCopyStatusResetCts?.Cancel();
        codeCopyStatusResetCts?.Dispose();
        incomingRequestTimeoutCts?.Cancel();
        incomingRequestTimeoutCts?.Dispose();
        _ = sessionRuntime.ResetAsync();
    }

    private void RegenerateCode()
    {
        sessionCode = SessionCode.CreateRandom();
        OnPropertyChanged(nameof(ShareCode));

        simulatedIncomingRequest = false;
        HasIncomingRequest = false;
        IsRequestAllowed = false;
        ShowChatNotice = false;
        ChatDraft = string.Empty;
        ChatMessages.Clear();
        OnPropertyChanged(nameof(HasChatMessages));
        OnPropertyChanged(nameof(ShowNoMessagesPlaceholder));
        CodeCopyStatusText = "Tell this code to your helper.";
        ConnectionStatus = "Waiting for helper…";
        ConnectionState = "Waiting";

        StartHosting();
    }

    private void SimulateIncomingRequest()
    {
        simulatedIncomingRequest = true;
        HasIncomingRequest = true;
        IsRequestAllowed = false;
        ConnectionStatus = "Helper on this PC wants to connect. Click Allow.";
        ConnectionState = "IncomingRequest";
    }

    private void ToggleTroubleshooting()
    {
        ShowTroubleshooting = !ShowTroubleshooting;
    }

    private async Task RetryAsync()
    {
        incomingRequestTimeoutCts?.Cancel();
        incomingRequestTimeoutCts?.Dispose();
        incomingRequestTimeoutCts = null;

        await sessionRuntime.ResetAsync();

        await UiThreadDispatch.RunAsync(() =>
        {
            HasIncomingRequest = false;
            IsRequestAllowed = false;
            ShowChatNotice = false;
            ConnectionStatus = "Waiting for helper…";
            ConnectionState = "Waiting";
        });

        StartHosting();
    }

    private bool CanOpenDiagnostics()
    {
        return openDiagnosticsAction is not null;
    }

    private void OpenDiagnostics()
    {
        openDiagnosticsAction?.Invoke();
    }

    public void NotifyCodeCopied()
    {
        _ = ShowTransientCodeCopyStatusAsync("Copied. Tell this code to your helper.");
    }

    public void NotifyCodeCopyFailed()
    {
        _ = ShowTransientCodeCopyStatusAsync("Could not copy the code. Please read it to your helper.");
    }

    private async Task CopyCodeAsync()
    {
        if (clipboardService is null)
        {
            NotifyCodeCopyFailed();
            return;
        }

        try
        {
            await clipboardService.SetTextAsync(ShareCode);
            NotifyCodeCopied();
        }
        catch
        {
            NotifyCodeCopyFailed();
        }
    }

    private bool CanAllowIncomingRequest()
    {
        return HasIncomingRequest && !IsRequestAllowed;
    }

    private bool CanDeclineIncomingRequest()
    {
        return HasIncomingRequest && !IsRequestAllowed;
    }

    private bool CanSendChat()
    {
        return !string.IsNullOrWhiteSpace(ChatDraft) && sessionRuntime.CanSendChat;
    }

    private void AllowIncomingRequest()
    {
        if (!CanAllowIncomingRequest())
        {
            return;
        }
        LogReliability(SessionReliabilityStage.Approved);
        LogReliability(SessionReliabilityStage.Completed);

        if (simulatedIncomingRequest)
        {
            simulatedIncomingRequest = false;
            CancelIncomingRequestTimeout();
            HasIncomingRequest = false;
            IsRequestAllowed = true;
            ShowChatNotice = false;
            ConnectionStatus = transportConfig.AllowStatusText;
            ConnectionState = "Connected";
            return;
        }

        _ = ApproveIncomingRequestAsync();
    }

    private async Task SendChatAsync()
    {
        var sent = await sessionRuntime.TrySendChatTextAsync(ChatDraft, CancellationToken.None);
        if (sent is null)
        {
            return;
        }

        ChatDraft = string.Empty;
        ShowChatNotice = false;
        AddChatLine(sent.Value.Text, isLocal: true);
    }

    private void CancelAndGoBack()
    {
        cancelAction();
    }

    private async Task DeclineIncomingRequestAsync()
    {
        if (!CanDeclineIncomingRequest())
        {
            return;
        }

        try
        {
            await sessionRuntime.RejectAsync(CancellationToken.None);
        }
        catch
        {
            // Best-effort. Runtime disconnect/reject events will reconcile state.
        }

        CancelIncomingRequestTimeout();
        HasIncomingRequest = false;
        IsRequestAllowed = false;
        ConnectionStatus = "Waiting for helper…";
        ConnectionState = "Waiting";
    }

    private void StartHosting()
    {
        if (IsStartupBlocked)
        {
            return;
        }

        simulatedIncomingRequest = false;
        CancelIncomingRequestTimeout();
        reliabilityAttempt = SessionReliabilityLog.StartAttempt("Helpee", transportConfig.Key);
        sessionRuntime.SetReliabilityAttempt(reliabilityAttempt);
        LogReliability(SessionReliabilityStage.CodeGenerated);
        LogReliability(SessionReliabilityStage.DiscoveryStarted);

        AppLog.Info($"Helpee hosting using {transportConfig.Key} with code {sessionCode.Digits}");
        _ = StartHostingAsync();
    }

    private async Task StartHostingAsync()
    {
        try
        {
            await sessionRuntime.ResetAsync();
            await sessionRuntime.StartHelpeeAsync(sessionCode, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // No-op.
        }
        catch
        {
            if (!HasIncomingRequest && !IsRequestAllowed)
            {
                await UiThreadDispatch.RunAsync(() =>
                {
                    ConnectionStatus = "Could not start. Try a new code.";
                    ConnectionState = "Disconnected";
                });
            }
        }
    }

    private async Task ApproveIncomingRequestAsync()
    {
        await sessionRuntime.ApproveAsync(CancellationToken.None);
        await UiThreadDispatch.RunAsync(() =>
        {
            ShowChatNotice = false;
            SyncFromRuntime();
        });
    }

    private void OnIncomingJoinRequestAvailable(object? sender, EventArgs e)
    {
        LogReliability(SessionReliabilityStage.IncomingJoinRequest);
        StartIncomingRequestTimeout();
    }

    private void OnRuntimeDisconnected(object? sender, EventArgs e)
    {
        if (disposed)
        {
            return;
        }

        _ = UiThreadDispatch.RunAsync(() =>
        {
            if (!HasIncomingRequest && !IsRequestAllowed)
            {
                var (errorCode, errorHint) = GetReliabilityError();
                LogReliability(SessionReliabilityStage.Disconnected, errorCode, errorHint);
            }

            SyncFromRuntime();
        });
    }

    private void OnChatMessageReceived(object? sender, ChatMessageEventArgs e)
    {
        _ = UiThreadDispatch.RunAsync(() =>
        {
            AddChatLine(e.Message.Text, isLocal: false);
        });
    }

    private void OnChatMessageReceivedBeforeApproved(object? sender, EventArgs e)
    {
        _ = UiThreadDispatch.RunAsync(() =>
        {
            if (ConnectionState != "Connected")
            {
                ShowChatNotice = true;
            }
        });
    }

    private void OnChatStateChanged(object? sender, EventArgs e)
    {
        _ = UiThreadDispatch.RunAsync(() =>
        {
            OnPropertyChanged(nameof(IsChatReady));
            SendChatCommand.NotifyCanExecuteChanged();
        });
    }

    private void AddChatLine(string text, bool isLocal)
    {
        ChatMessages.Add(new ChatLineViewModel
        {
            Text = text,
            IsLocal = isLocal,
        });

        while (ChatMessages.Count > 100)
        {
            ChatMessages.RemoveAt(0);
        }

        OnPropertyChanged(nameof(HasChatMessages));
        OnPropertyChanged(nameof(ShowNoMessagesPlaceholder));
    }

    private void LogReliability(SessionReliabilityStage stage, string? errorCode = null, string? errorHint = null)
    {
        if (reliabilityAttempt is null)
        {
            return;
        }

        SessionReliabilityLog.RecordStage(reliabilityAttempt, stage, errorCode, errorHint);
    }

    private (string? Code, string? Hint) GetReliabilityError()
    {
        if (!string.Equals(transportConfig.Key, "NKN", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        var lastError = NknRuntimeDiagnostics.Snapshot().LastError;
        if (string.IsNullOrWhiteSpace(lastError) || string.Equals(lastError, "(none)", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        return (lastError, "The connection stopped. Try a new code.");
    }

    private void OnSessionRuntimeStateChanged(object? sender, SessionRuntimeStateChangedEventArgs e)
    {
        if (disposed)
        {
            return;
        }

        _ = UiThreadDispatch.RunAsync(SyncFromRuntime);
    }

    private void SyncFromRuntime()
    {
        switch (sessionRuntime.State)
        {
            case SessionRuntimeState.IncomingJoinRequest:
                HasIncomingRequest = true;
                IsRequestAllowed = false;
                ConnectionStatus = sessionRuntime.StatusText;
                ConnectionState = "IncomingRequest";
                break;

            case SessionRuntimeState.Connected:
                CancelIncomingRequestTimeout();
                HasIncomingRequest = false;
                IsRequestAllowed = true;
                ConnectionStatus = transportConfig.AllowStatusText;
                ConnectionState = "Connected";
                break;

            case SessionRuntimeState.Disconnected:
                CancelIncomingRequestTimeout();
                if (!HasIncomingRequest && !IsRequestAllowed)
                {
                    ConnectionStatus = string.IsNullOrWhiteSpace(sessionRuntime.StatusText)
                        ? "Connection lost."
                        : sessionRuntime.StatusText;
                    ConnectionState = "Failed";
                }
                break;

            case SessionRuntimeState.Waiting:
                if (!HasIncomingRequest && !IsRequestAllowed)
                {
                    ConnectionStatus = string.IsNullOrWhiteSpace(sessionRuntime.StatusText)
                        ? "Waiting for helper…"
                        : sessionRuntime.StatusText;
                    ConnectionState = "Waiting";
                }
                break;

            case SessionRuntimeState.Failed:
                CancelIncomingRequestTimeout();
                HasIncomingRequest = false;
                IsRequestAllowed = false;
                ConnectionStatus = string.IsNullOrWhiteSpace(sessionRuntime.StatusText)
                    ? "Connection lost."
                    : sessionRuntime.StatusText;
                ConnectionState = "Failed";
                break;
        }

        OnPropertyChanged(nameof(ShowRetryAction));
        OnPropertyChanged(nameof(ShowOpenDiagnosticsLink));
        RetryCommand.NotifyCanExecuteChanged();
        OpenDiagnosticsCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsChatReady));
        SendChatCommand.NotifyCanExecuteChanged();
        AllowCommand.NotifyCanExecuteChanged();
        DeclineCommand.NotifyCanExecuteChanged();
    }

    private async Task ShowTransientCodeCopyStatusAsync(string text)
    {
        codeCopyStatusResetCts?.Cancel();
        codeCopyStatusResetCts?.Dispose();
        codeCopyStatusResetCts = new CancellationTokenSource();
        var ct = codeCopyStatusResetCts.Token;

        await UiThreadDispatch.RunAsync(() =>
        {
            CodeCopyStatusText = text;
        });

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested)
        {
            return;
        }

        await UiThreadDispatch.RunAsync(() =>
        {
            CodeCopyStatusText = "Tell this code to your helper.";
        });
    }

    private void InitializeStartupAvailabilityState()
    {
        if (!string.Equals(transportConfig.Key, "NKN", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (transportConfig.HasStartupWarning || UserErrorMapper.IsNknStartFailure(NknRuntimeDiagnostics.Snapshot().LastError))
        {
            IsStartupBlocked = true;
            ConnectionStatus = UserErrorMapper.NknStartFailedReinstall();
            ConnectionState = "Failed";
        }
    }

    private void StartIncomingRequestTimeout()
    {
        CancelIncomingRequestTimeout();

        incomingRequestTimeoutCts = new CancellationTokenSource();
        var ct = incomingRequestTimeoutCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(incomingRequestTimeout, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (ct.IsCancellationRequested || disposed)
            {
                return;
            }

            try
            {
                await sessionRuntime.RejectAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort.
            }

            await UiThreadDispatch.RunAsync(() =>
            {
                if (!IsIncomingRequestView)
                {
                    return;
                }

                HasIncomingRequest = false;
                IsRequestAllowed = false;
                ConnectionStatus = "No response yet.";
                ConnectionState = "Waiting";
            });
        });
    }

    private void CancelIncomingRequestTimeout()
    {
        incomingRequestTimeoutCts?.Cancel();
        incomingRequestTimeoutCts?.Dispose();
        incomingRequestTimeoutCts = null;
    }
}
