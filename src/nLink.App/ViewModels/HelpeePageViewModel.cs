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
    private readonly StatusPresenter statusPresenter;
    private readonly bool ownsStatusPresenter;
    private readonly IClipboardService? clipboardService;
    private SessionCode sessionCode = SessionCode.CreateRandom();
    private bool hasIncomingRequest;
    private bool isRequestAllowed;
    private bool showTroubleshooting;
    private bool showChatNotice;
    private string connectionStatus = "Waiting for helper…";
    private string connectionState = "Waiting";
    private string chatDraft = string.Empty;
    private string failureTitle = string.Empty;
    private string failureMessage = string.Empty;
    private string failureActionText = string.Empty;
    private bool showTransientBanner;
    private string transientBannerText = string.Empty;
    private bool canCancelTransient;
    private bool simulatedIncomingRequest;
    private SessionReliabilityAttempt? reliabilityAttempt;
    private readonly InlineTransientText copyFeedback = new();
    private CancellationTokenSource? incomingRequestTimeoutCts;
    private readonly TimeSpan incomingRequestTimeout;
    private bool startupBlocked;
    private bool hadConnectedSessionForCurrentCode;
    private bool autoRegeneratingAfterDisconnect;
    private UserFacingStatus bannerStatus = UserFacingStatus.IdleStatus;
    private bool disposed;

    public HelpeePageViewModel(
        Action cancelAction,
        TransportRuntimeConfig transportConfig,
        SessionRuntime sessionRuntime,
        Action? openDiagnosticsAction = null,
        IClipboardService? clipboardService = null,
        ShareMessageConfig? shareMessageConfig = null,
        StatusPresenter? statusPresenter = null,
        TimeSpan? incomingRequestTimeout = null)
    {
        this.cancelAction = cancelAction;
        this.openDiagnosticsAction = openDiagnosticsAction;
        this.transportConfig = transportConfig;
        this.sessionRuntime = sessionRuntime;
        this.statusPresenter = statusPresenter ?? new StatusPresenter(sessionRuntime);
        ownsStatusPresenter = statusPresenter is null;
        this.clipboardService = clipboardService;
        this.incomingRequestTimeout = incomingRequestTimeout ?? DefaultIncomingRequestTimeout;
        _ = shareMessageConfig;

        ChatMessages = new ObservableCollection<ChatLineViewModel>();

        sessionRuntime.StateChanged += OnSessionRuntimeStateChanged;
        sessionRuntime.TransientStatusChanged += OnTransientStatusChanged;
        sessionRuntime.IncomingJoinRequestAvailable += OnIncomingJoinRequestAvailable;
        sessionRuntime.Disconnected += OnRuntimeDisconnected;
        sessionRuntime.ChatMessageReceived += OnChatMessageReceived;
        sessionRuntime.ChatMessageReceivedBeforeApproved += OnChatMessageReceivedBeforeApproved;
        sessionRuntime.ChatStateChanged += OnChatStateChanged;
        this.statusPresenter.StatusChanged += OnStatusPresenterChanged;

        RegenerateCodeCommand = new RelayCommand(RegenerateCode);
        CopyCodeCommand = new AsyncRelayCommand(CopyCodeAsync);
        SimulateIncomingRequestCommand = new RelayCommand(SimulateIncomingRequest);
        ToggleTroubleshootingCommand = new RelayCommand(ToggleTroubleshooting);
        AllowCommand = new RelayCommand(AllowIncomingRequest, CanAllowIncomingRequest);
        DeclineCommand = new AsyncRelayCommand(DeclineIncomingRequestAsync, CanDeclineIncomingRequest);
        SendChatCommand = new AsyncRelayCommand(SendChatAsync, CanSendChat);
        RetryCommand = new AsyncRelayCommand(RetryAsync);
        CancelTransientCommand = new AsyncRelayCommand(CancelTransientAsync, CanCancelTransientOperation);
        OpenDiagnosticsCommand = new RelayCommand(OpenDiagnostics, CanOpenDiagnostics);
        CancelCommand = new RelayCommand(CancelAndGoBack);

        InitializeStartupAvailabilityState();
        BannerStatus = this.statusPresenter.CurrentStatus;
        if (!IsStartupBlocked)
        {
            StartHosting();
        }
        SyncTransientStatusFromRuntime();
    }

    public string PageTitle => IsIncomingRequestView ? "Someone wants to connect" : "Your code";
    public bool ShowPageHeader => !IsConnectedView;

    public string PageSubtitle => IsIncomingRequestView ? string.Empty : "Tell this code to your helper.";
    public bool ShowPageSubtitle => ConnectionState == "Waiting" && !IsStartupBlocked;

    public string ShareCode => sessionCode.DisplayText;

    public string IncomingHelperName => "Helper on this PC";

    public InlineTransientText CopyFeedback => copyFeedback;
    public bool ShowCopyFeedbackInline => ShowWaitingPanel && copyFeedback.IsVisible;

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
                OnPropertyChanged(nameof(ShowStartupBlockedPanel));
                OnPropertyChanged(nameof(ShowWaitingStatusLine));
                OnPropertyChanged(nameof(ShowWaitingCodeActions));
                OnPropertyChanged(nameof(ShowBackButton));
                OnPropertyChanged(nameof(PageTitle));
                OnPropertyChanged(nameof(PageSubtitle));
                OnPropertyChanged(nameof(ShowPageSubtitle));
                OnPropertyChanged(nameof(ShowPageHeader));
                OnPropertyChanged(nameof(StatusLineText));
                OnPropertyChanged(nameof(SecondaryActionText));
                OnPropertyChanged(nameof(ShowChatSection));
                OnPropertyChanged(nameof(ShowFailurePanel));
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
    public bool ShowFailurePanel => (!string.IsNullOrWhiteSpace(FailureTitle) || !string.IsNullOrWhiteSpace(FailureMessage)) &&
                                    (IsStartupBlocked || ConnectionState == "Failed");
    public bool ShowStartupBlockedPanel => IsStartupBlocked && !ShowFailurePanel;
    public bool ShowWaitingStatusLine => !ShowFailurePanel;

    public bool ShowBackButton => !IsConnectedView && !IsIncomingRequestView && !IsStartupBlocked;
    public UserFacingStatus BannerStatus
    {
        get => bannerStatus;
        private set
        {
            if (SetProperty(ref bannerStatus, value))
            {
                OnPropertyChanged(nameof(ShowStatusBanner));
            }
        }
    }

    public bool ShowStatusBanner =>
        BannerStatus.Kind is not UserStatusKind.Idle and not UserStatusKind.Connected &&
        !IsIncomingRequestView;
    public string? StatusBannerFailureCategory => NormalizeBannerDetail(sessionRuntime.LastTransportFailure?.Category.ToString());
    public string? StatusBannerSessionCorrelationId => NormalizeBannerDetail(sessionRuntime.LastTransportFailure?.CorrelationId);
    public string? StatusBannerLastConnectDuration => FormatBannerDuration(sessionRuntime.GetDiagnosticsSnapshot().LastConnectDurationMs);
    public string? StatusBannerLastHandshakeDuration => FormatBannerDuration(sessionRuntime.GetDiagnosticsSnapshot().LastHandshakeDurationMs);
    public string? StatusBannerBridgeState => BuildBannerBridgeState();

    public string StatusLineText => IsIncomingRequestView
        ? "Waiting for you to allow."
        : IsConnectedView
            ? ConnectionStatus
            : ConnectionStatus;

    public string SecondaryActionText => IsConnectedView ? "Disconnect" : "New code";

    public string FailureTitle
    {
        get => failureTitle;
        private set => SetProperty(ref failureTitle, value);
    }

    public string FailureMessage
    {
        get => failureMessage;
        private set => SetProperty(ref failureMessage, value);
    }

    public string FailureActionText
    {
        get => failureActionText;
        private set => SetProperty(ref failureActionText, value);
    }

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
                OnPropertyChanged(nameof(ShowStartupBlockedPanel));
                OnPropertyChanged(nameof(ShowWaitingStatusLine));
                OnPropertyChanged(nameof(ShowWaitingCodeActions));
                OnPropertyChanged(nameof(ShowDevTroubleshooting));
                OnPropertyChanged(nameof(ShowBackButton));
                OnPropertyChanged(nameof(ShowRetryAction));
                OnPropertyChanged(nameof(ShowOpenDiagnosticsLink));
                OnPropertyChanged(nameof(ShowPageSubtitle));
                OnPropertyChanged(nameof(ShowFailurePanel));
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
    public IAsyncRelayCommand CancelTransientCommand { get; }

    public IRelayCommand OpenDiagnosticsCommand { get; }

    public IRelayCommand CancelCommand { get; }
    public IRelayCommand EndSessionCommand => CancelCommand;
    public IRelayCommand StatusBannerCopyDiagnosticsCommand => OpenDiagnosticsCommand;
    public IAsyncRelayCommand StatusBannerCancelCommand => CancelTransientCommand;

    public bool ShowRetryAction => !IsStartupBlocked && ConnectionState == "Failed";
    public bool ShowTransientBanner
    {
        get => showTransientBanner;
        private set => SetProperty(ref showTransientBanner, value);
    }

    public string TransientBannerText
    {
        get => transientBannerText;
        private set => SetProperty(ref transientBannerText, value);
    }

    public bool CanCancelTransient
    {
        get => canCancelTransient;
        private set
        {
            if (SetProperty(ref canCancelTransient, value))
            {
                CancelTransientCommand.NotifyCanExecuteChanged();
            }
        }
    }

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
        sessionRuntime.TransientStatusChanged -= OnTransientStatusChanged;
        sessionRuntime.IncomingJoinRequestAvailable -= OnIncomingJoinRequestAvailable;
        sessionRuntime.Disconnected -= OnRuntimeDisconnected;
        sessionRuntime.ChatMessageReceived -= OnChatMessageReceived;
        sessionRuntime.ChatMessageReceivedBeforeApproved -= OnChatMessageReceivedBeforeApproved;
        sessionRuntime.ChatStateChanged -= OnChatStateChanged;
        statusPresenter.StatusChanged -= OnStatusPresenterChanged;
        if (ownsStatusPresenter)
        {
            statusPresenter.Dispose();
        }
        sessionRuntime.SetReliabilityAttempt(null);
        copyFeedback.Dispose();
        incomingRequestTimeoutCts?.Cancel();
        incomingRequestTimeoutCts?.Dispose();
        _ = sessionRuntime.ResetAsync();
    }

    private void RegenerateCode()
    {
        sessionCode = SessionCode.CreateRandom();
        OnPropertyChanged(nameof(ShareCode));
        hadConnectedSessionForCurrentCode = false;
        autoRegeneratingAfterDisconnect = false;

        simulatedIncomingRequest = false;
        HasIncomingRequest = false;
        IsRequestAllowed = false;
        ShowChatNotice = false;
        ChatDraft = string.Empty;
        ChatMessages.Clear();
        OnPropertyChanged(nameof(HasChatMessages));
        OnPropertyChanged(nameof(ShowNoMessagesPlaceholder));
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

    private bool CanCancelTransientOperation()
    {
        return ShowTransientBanner && CanCancelTransient;
    }

    private async Task CancelTransientAsync()
    {
        try
        {
            await sessionRuntime.CancelTransientAsync();
        }
        catch
        {
            // best effort UX action
        }
        finally
        {
            SyncTransientStatusFromRuntime();
        }
    }

    public void NotifyCodeCopied()
    {
        copyFeedback.Show("Copied. Tell this code to your helper.");
    }

    public void NotifyCodeCopyFailed()
    {
        copyFeedback.Show("Could not copy the code. Please read it to your helper.");
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
        var draft = ChatDraft;
        var optimisticText = draft.Trim();
        if (string.IsNullOrWhiteSpace(optimisticText))
        {
            return;
        }

        ChatDraft = string.Empty;
        ShowChatNotice = false;
        var optimisticLine = AddChatLine(optimisticText, isLocal: true);

        var sent = await sessionRuntime.TrySendChatTextAsync(draft, CancellationToken.None);
        if (sent is null)
        {
            ChatMessages.Remove(optimisticLine);
            OnPropertyChanged(nameof(HasChatMessages));
            OnPropertyChanged(nameof(ShowNoMessagesPlaceholder));
            if (string.IsNullOrWhiteSpace(ChatDraft))
            {
                ChatDraft = draft;
            }
            return;
        }
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

    private ChatLineViewModel AddChatLine(string text, bool isLocal)
    {
        var line = new ChatLineViewModel
        {
            Text = text,
            IsLocal = isLocal,
        };
        ChatMessages.Add(line);

        while (ChatMessages.Count > 100)
        {
            ChatMessages.RemoveAt(0);
        }

        OnPropertyChanged(nameof(HasChatMessages));
        OnPropertyChanged(nameof(ShowNoMessagesPlaceholder));
        return line;
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

    private void OnTransientStatusChanged(object? sender, SessionRuntimeTransientStatusChangedEventArgs e)
    {
        if (disposed)
        {
            return;
        }

        _ = UiThreadDispatch.RunAsync(SyncTransientStatusFromRuntime);
    }

    private void OnStatusPresenterChanged(object? sender, UserFacingStatusChangedEventArgs e)
    {
        if (disposed)
        {
            return;
        }

        _ = UiThreadDispatch.RunAsync(() =>
        {
            BannerStatus = e.Status;
            NotifyStatusBannerDetailChanged();
        });
    }

    private void SyncFromRuntime()
    {
        switch (sessionRuntime.State)
        {
            case SessionRuntimeState.IncomingJoinRequest:
                ClearFailurePresentation();
                HasIncomingRequest = true;
                IsRequestAllowed = false;
                ConnectionStatus = sessionRuntime.StatusText;
                ConnectionState = "IncomingRequest";
                break;

            case SessionRuntimeState.Connected:
                ClearFailurePresentation();
                CancelIncomingRequestTimeout();
                HasIncomingRequest = false;
                IsRequestAllowed = true;
                hadConnectedSessionForCurrentCode = true;
                ConnectionStatus = transportConfig.AllowStatusText;
                ConnectionState = "Connected";
                break;

            case SessionRuntimeState.Disconnected:
                if (TryAutoRegenerateAfterConnectedSessionEnd())
                {
                    break;
                }
                CancelIncomingRequestTimeout();
                if (!HasIncomingRequest && !IsRequestAllowed)
                {
                    ConnectionStatus = string.IsNullOrWhiteSpace(sessionRuntime.StatusText)
                        ? "Connection lost."
                        : sessionRuntime.StatusText;
                    ConnectionState = "Failed";
                    ApplyFailurePresentation();
                }
                break;

            case SessionRuntimeState.Waiting:
                if (!HasIncomingRequest && !IsRequestAllowed)
                {
                    ConnectionStatus = string.IsNullOrWhiteSpace(sessionRuntime.StatusText)
                        ? "Waiting for helper…"
                        : sessionRuntime.StatusText;
                    ConnectionState = "Waiting";
                    ClearFailurePresentation();
                }
                break;

            case SessionRuntimeState.Failed:
                if (TryAutoRegenerateAfterConnectedSessionEnd())
                {
                    break;
                }
                CancelIncomingRequestTimeout();
                HasIncomingRequest = false;
                IsRequestAllowed = false;
                ConnectionStatus = string.IsNullOrWhiteSpace(sessionRuntime.StatusText)
                    ? "Connection lost."
                    : sessionRuntime.StatusText;
                ConnectionState = "Failed";
                ApplyFailurePresentation();
                break;
        }

        OnPropertyChanged(nameof(ShowRetryAction));
        OnPropertyChanged(nameof(ShowOpenDiagnosticsLink));
        OnPropertyChanged(nameof(ShowFailurePanel));
        RetryCommand.NotifyCanExecuteChanged();
        OpenDiagnosticsCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsChatReady));
        SendChatCommand.NotifyCanExecuteChanged();
        AllowCommand.NotifyCanExecuteChanged();
        DeclineCommand.NotifyCanExecuteChanged();
        SyncTransientStatusFromRuntime();
        NotifyStatusBannerDetailChanged();
    }

    private void SyncTransientStatusFromRuntime()
    {
        ShowTransientBanner = sessionRuntime.IsTransientStatusVisible && !IsConnectedView;
        TransientBannerText = sessionRuntime.TransientStatusText;
        CanCancelTransient = sessionRuntime.CanCancelTransientStatus;
    }

    private void ApplyFailurePresentation()
    {
        // Error presentation is handled centrally by StatusPresenter -> StatusBanner.
    }

    private void ClearFailurePresentation()
    {
        // Error presentation is handled centrally by StatusPresenter -> StatusBanner.
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
            var failure = TransportFailure.Create(
                TransportFailureCategory.BridgeStartFailure,
                "Connection system isn’t available. Please reinstall.",
                rawError: NknRuntimeDiagnostics.Snapshot().LastError,
                isTransient: false);
            _ = sessionRuntime.FailAsync(failure, UserErrorMapper.NknStartFailedReinstall());
        }
    }

    private void NotifyStatusBannerDetailChanged()
    {
        OnPropertyChanged(nameof(StatusBannerFailureCategory));
        OnPropertyChanged(nameof(StatusBannerSessionCorrelationId));
        OnPropertyChanged(nameof(StatusBannerLastConnectDuration));
        OnPropertyChanged(nameof(StatusBannerLastHandshakeDuration));
        OnPropertyChanged(nameof(StatusBannerBridgeState));
    }

    private string? BuildBannerBridgeState()
    {
        if (!string.Equals(transportConfig.Key, "NKN", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var snapshot = NknRuntimeDiagnostics.Snapshot();
        if (snapshot.BridgePid > 0)
        {
            return $"Running (PID {snapshot.BridgePid})";
        }

        if (snapshot.BridgeLastExitCode >= 0)
        {
            return $"Exited (code {snapshot.BridgeLastExitCode})";
        }

        if (!string.IsNullOrWhiteSpace(snapshot.BridgeLastExitReason) &&
            !string.Equals(snapshot.BridgeLastExitReason, "(none)", StringComparison.OrdinalIgnoreCase))
        {
            return $"Exited ({snapshot.BridgeLastExitReason})";
        }

        return null;
    }

    private static string? FormatBannerDuration(double? value)
        => value.HasValue ? $"{value.Value:F0} ms" : null;

    private static string? NormalizeBannerDetail(string? value)
        => string.IsNullOrWhiteSpace(value) || string.Equals(value, "(none)", StringComparison.OrdinalIgnoreCase)
            ? null
            : value;

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

    private bool TryAutoRegenerateAfterConnectedSessionEnd()
    {
        if (!hadConnectedSessionForCurrentCode || autoRegeneratingAfterDisconnect || IsStartupBlocked)
        {
            return false;
        }

        autoRegeneratingAfterDisconnect = true;
        try
        {
            RegenerateCode();
            return true;
        }
        finally
        {
            autoRegeneratingAfterDisconnect = false;
        }
    }
}
