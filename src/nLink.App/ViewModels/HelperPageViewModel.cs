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

public sealed class HelperPageViewModel : ViewModelBase, IDisposable, IChatPanelBindings
{
    private static readonly TimeSpan DefaultConnectFailureCooldown = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultApprovalTimeout = TimeSpan.FromSeconds(20);

    private readonly Action cancelAction;
    private readonly Action? openDiagnosticsAction;
    private readonly TransportRuntimeConfig transportConfig;
    private readonly SessionRuntime sessionRuntime;
    private readonly StatusPresenter statusPresenter;
    private readonly bool ownsStatusPresenter;
    private readonly IClipboardService? clipboardService;
    private readonly ShareMessageConfig shareMessageConfig;

    private string codeInput = string.Empty;
    private string statusText = string.Empty;
    private string connectionState = "Idle";
    private string chatDraft = string.Empty;
    private string failureTitle = string.Empty;
    private string failureMessage = string.Empty;
    private string failureActionText = string.Empty;
    private bool showTransientBanner;
    private string transientBannerText = string.Empty;
    private bool canCancelTransient;
    private bool isConnecting;
    private bool showChatNotice;
    private bool startupBlocked;
    private UserFacingStatus bannerStatus = UserFacingStatus.IdleStatus;
    private CancellationTokenSource? connectCts;
    private readonly InlineTransientText copyFeedback = new();
    private readonly Func<DateTimeOffset> nowProvider;
    private readonly TimeSpan connectFailureCooldown;
    private readonly TimeSpan approvalTimeout;
    private DateTimeOffset lastFailedAttemptUtc = DateTimeOffset.MinValue;
    private TaskCompletionSource<HelperConnectOutcome>? connectOutcome;
    private SessionReliabilityAttempt? reliabilityAttempt;
    private bool disposed;

    public HelperPageViewModel(
        Action cancelAction,
        TransportRuntimeConfig transportConfig,
        SessionRuntime sessionRuntime,
        Action? openDiagnosticsAction = null,
        IClipboardService? clipboardService = null,
        ShareMessageConfig? shareMessageConfig = null,
        StatusPresenter? statusPresenter = null,
        TimeSpan? approvalTimeout = null,
        TimeSpan? connectFailureCooldown = null,
        Func<DateTimeOffset>? nowProvider = null)
    {
        this.cancelAction = cancelAction;
        this.openDiagnosticsAction = openDiagnosticsAction;
        this.transportConfig = transportConfig;
        this.sessionRuntime = sessionRuntime;
        this.statusPresenter = statusPresenter ?? new StatusPresenter(sessionRuntime);
        ownsStatusPresenter = statusPresenter is null;
        this.clipboardService = clipboardService;
        this.shareMessageConfig = shareMessageConfig ?? new ShareMessageConfig(null);
        this.approvalTimeout = approvalTimeout ?? DefaultApprovalTimeout;
        this.connectFailureCooldown = connectFailureCooldown ?? DefaultConnectFailureCooldown;
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);

        ChatMessages = new ObservableCollection<ChatLineViewModel>();

        sessionRuntime.StateChanged += OnSessionRuntimeStateChanged;
        sessionRuntime.TransientStatusChanged += OnTransientStatusChanged;
        sessionRuntime.Approved += OnApproved;
        sessionRuntime.Rejected += OnRejected;
        sessionRuntime.Disconnected += OnDisconnected;
        sessionRuntime.ChatMessageReceived += OnChatMessageReceived;
        sessionRuntime.ChatMessageReceivedBeforeApproved += OnChatMessageReceivedBeforeApproved;
        sessionRuntime.ChatStateChanged += OnChatStateChanged;
        this.statusPresenter.StatusChanged += OnStatusPresenterChanged;
        copyFeedback.PropertyChanged += OnCopyFeedbackPropertyChanged;

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, CanConnect);
        CopyInstallMessageCommand = new AsyncRelayCommand(CopyInstallMessageAsync);
        SendFileCommand = new RelayCommand(RequestSendFileWindow);
        SendChatCommand = new AsyncRelayCommand(SendChatAsync, CanSendChat);
        RetryCommand = new AsyncRelayCommand(RetryAsync, CanRetry);
        CancelTransientCommand = new AsyncRelayCommand(CancelTransientAsync, CanCancelTransientOperation);
        OpenDiagnosticsCommand = new RelayCommand(OpenDiagnostics, CanOpenDiagnostics);
        CancelCommand = new RelayCommand(CancelAndGoBack);

        InitializeStartupAvailabilityState();
        BannerStatus = this.statusPresenter.CurrentStatus;
        SyncTransientStatusFromRuntime();
    }

    public string PageTitle => "Enter the 6-digit code";
    public bool ShowPageHeader => !IsConnectedView;

    public string CodeInput
    {
        get => codeInput;
        set
        {
            var incoming = value ?? string.Empty;
            var digits = SessionCode.NormalizeDigits(value);
            if (digits.Length > 6)
            {
                digits = digits[..6];
            }

            var formatted = SessionCode.FormatPartial(digits);
            if (SetProperty(ref codeInput, formatted))
            {
                ConnectCommand.NotifyCanExecuteChanged();
                SendChatCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(ShowChatPanel));
                OnPropertyChanged(nameof(ShowChatConnectionHint));
            }
            else if (!string.Equals(incoming, formatted, StringComparison.Ordinal))
            {
                // Force the TextBox to rebind to the canonical 6-digit formatted value
                // when the user types/pastes extra digits that normalize to the same text.
                OnPropertyChanged(nameof(CodeInput));
            }
        }
    }

    public string StatusText
    {
        get => statusText;
        private set
        {
            if (SetProperty(ref statusText, value))
            {
                OnPropertyChanged(nameof(ShowStatusText));
            }
        }
    }

    public string ConnectionState
    {
        get => connectionState;
        private set
        {
            if (SetProperty(ref connectionState, value))
            {
                OnPropertyChanged(nameof(IsConnectedView));
                OnPropertyChanged(nameof(ShowConnectedPanel));
                OnPropertyChanged(nameof(ShowChatPanel));
                OnPropertyChanged(nameof(ShowChatConnectionHint));
                OnPropertyChanged(nameof(ShowMainControls));
                OnPropertyChanged(nameof(ShowConnectAction));
                OnPropertyChanged(nameof(ShowStartupBlockedPanel));
                OnPropertyChanged(nameof(ShowInlineStatusText));
                OnPropertyChanged(nameof(ShowFailurePanel));
                OnPropertyChanged(nameof(ShowCopyFeedbackInline));
                OnPropertyChanged(nameof(ShowBackButton));
                OnPropertyChanged(nameof(ShowPageHeader));
            }
        }
    }

    public bool IsConnecting
    {
        get => isConnecting;
        private set
        {
            if (SetProperty(ref isConnecting, value))
            {
                ConnectCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string HelperPageHelpText => "Ask them for the 6-digit code on their screen.";

    public string ConnectionMethodHint => transportConfig.HelperHintText;

    public ObservableCollection<ChatLineViewModel> ChatMessages { get; }

    public bool IsConnectedView => ConnectionState == "Connected";

    public bool ShowConnectedPanel => IsConnectedView;

    public bool ShowChatPanel => IsConnectedView;

    public bool ShowStatusText => !string.IsNullOrWhiteSpace(StatusText);
    public bool ShowInlineStatusText => ShowStatusText && !IsStartupBlocked && !IsConnectedView && !ShowFailurePanel;
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

    public bool ShowStatusBanner => BannerStatus.Kind is not UserStatusKind.Idle and not UserStatusKind.Connected;
    public string? StatusBannerFailureCategory => NormalizeBannerDetail(sessionRuntime.LastTransportFailure?.Category.ToString());
    public string? StatusBannerSessionCorrelationId => NormalizeBannerDetail(sessionRuntime.LastTransportFailure?.CorrelationId);
    public string? StatusBannerLastConnectDuration => FormatBannerDuration(sessionRuntime.GetDiagnosticsSnapshot().LastConnectDurationMs);
    public string? StatusBannerLastHandshakeDuration => FormatBannerDuration(sessionRuntime.GetDiagnosticsSnapshot().LastHandshakeDurationMs);
    public string? StatusBannerBridgeState => BuildBannerBridgeState();

    public bool IsStartupBlocked
    {
        get => startupBlocked;
        private set
        {
            if (SetProperty(ref startupBlocked, value))
            {
                OnPropertyChanged(nameof(ShowMainControls));
                OnPropertyChanged(nameof(ShowConnectAction));
                OnPropertyChanged(nameof(ShowRetryAction));
                OnPropertyChanged(nameof(ShowOpenDiagnosticsLink));
                OnPropertyChanged(nameof(ShowStartupBlockedPanel));
                OnPropertyChanged(nameof(ShowInlineStatusText));
                OnPropertyChanged(nameof(ShowFailurePanel));
                OnPropertyChanged(nameof(ShowCopyFeedbackInline));
                ConnectCommand.NotifyCanExecuteChanged();
                RetryCommand.NotifyCanExecuteChanged();
                OpenDiagnosticsCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool ShowMainControls => !IsStartupBlocked && !ShowRetryAction && !IsConnectedView;

    public bool ShowConnectAction => ShowMainControls && !ShowRetryAction;
    public bool ShowStartupBlockedPanel => IsStartupBlocked && !ShowFailurePanel;

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

    public bool ShowFailurePanel =>
        (!string.IsNullOrWhiteSpace(FailureTitle) || !string.IsNullOrWhiteSpace(FailureMessage)) &&
        (IsStartupBlocked || string.Equals(ConnectionState, "Failed", StringComparison.Ordinal));

    public string ChatPanelTitle => "Message";
    public bool HasChatMessages => ChatMessages.Count > 0;
    public bool ShowNoMessagesPlaceholder => !HasChatMessages;

    public bool ShowChatConnectionHint => false;

    public string ChatConnectionHintText => "Waiting for connection...";

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

    public bool ShowChatNotice
    {
        get => showChatNotice;
        private set => SetProperty(ref showChatNotice, value);
    }

    public string ChatNoticeText => "You received a message";

    public bool IsChatReady => sessionRuntime.CanSendChat;

    public IAsyncRelayCommand ConnectCommand { get; }

    public IAsyncRelayCommand CopyInstallMessageCommand { get; }

    public IRelayCommand SendFileCommand { get; }

    public IAsyncRelayCommand SendChatCommand { get; }

    public IAsyncRelayCommand RetryCommand { get; }
    public IAsyncRelayCommand CancelTransientCommand { get; }

    public IRelayCommand OpenDiagnosticsCommand { get; }

    public IRelayCommand CancelCommand { get; }
    public IRelayCommand EndSessionCommand => CancelCommand;
    public IRelayCommand StatusBannerCopyDiagnosticsCommand => OpenDiagnosticsCommand;
    public IAsyncRelayCommand StatusBannerCancelCommand => CancelTransientCommand;

    public event EventHandler? SendFileRequested;

    public bool ShowRetryAction => !IsStartupBlocked &&
                                   string.Equals(ConnectionState, "Failed", StringComparison.Ordinal) &&
                                   string.Equals(StatusText, "Connection lost.", StringComparison.Ordinal);

    public bool ShowOpenDiagnosticsLink =>
        openDiagnosticsAction is not null &&
        (IsStartupBlocked || string.Equals(StatusText, UserErrorMapper.NknStartFailedReinstall(), StringComparison.Ordinal));

    public InlineTransientText CopyFeedback => copyFeedback;
    public bool ShowCopyFeedbackInline => ShowMainControls && copyFeedback.IsVisible;

    public bool ShowBackButton => !IsConnectedView;
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

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        sessionRuntime.StateChanged -= OnSessionRuntimeStateChanged;
        sessionRuntime.TransientStatusChanged -= OnTransientStatusChanged;
        sessionRuntime.Approved -= OnApproved;
        sessionRuntime.Rejected -= OnRejected;
        sessionRuntime.Disconnected -= OnDisconnected;
        sessionRuntime.ChatMessageReceived -= OnChatMessageReceived;
        sessionRuntime.ChatMessageReceivedBeforeApproved -= OnChatMessageReceivedBeforeApproved;
        sessionRuntime.ChatStateChanged -= OnChatStateChanged;
        statusPresenter.StatusChanged -= OnStatusPresenterChanged;
        copyFeedback.PropertyChanged -= OnCopyFeedbackPropertyChanged;
        if (ownsStatusPresenter)
        {
            statusPresenter.Dispose();
        }
        sessionRuntime.SetReliabilityAttempt(null);
        _ = sessionRuntime.ResetAsync();

        connectCts?.Cancel();
        connectCts?.Dispose();
        connectCts = null;
        copyFeedback.Dispose();
    }

    private bool CanConnect()
    {
        return !IsStartupBlocked && !IsConnecting && SessionCode.TryParse(CodeInput, out _);
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

    private bool CanSendChat()
    {
        return !string.IsNullOrWhiteSpace(ChatDraft) && sessionRuntime.CanSendChat;
    }

    private bool CanRetry()
    {
        return !IsConnecting;
    }

    private bool CanOpenDiagnostics()
    {
        return openDiagnosticsAction is not null;
    }

    private async Task ConnectAsync()
    {
        if (IsStartupBlocked)
        {
            return;
        }

        if (IsInFailureCooldown())
        {
            return;
        }

        if (!SessionCode.TryParse(CodeInput, out var code))
        {
            StatusText = UserErrorMapper.HelperInvalidCode();
            ConnectionState = "InvalidCode";
            OnPropertyChanged(nameof(ShowChatConnectionHint));
            return;
        }

        connectCts?.Cancel();
        connectCts?.Dispose();
        connectCts = new CancellationTokenSource();
        reliabilityAttempt = SessionReliabilityLog.StartAttempt("Helper", transportConfig.Key);
        sessionRuntime.SetReliabilityAttempt(reliabilityAttempt);
        LogReliability(SessionReliabilityStage.DiscoveryStarted);

        await sessionRuntime.ResetAsync();

        AppLog.Info($"Helper join requested using {transportConfig.Key} with code {code.Digits}");

        IsConnecting = true;
        StatusText = "Connecting…";
        ConnectionState = "Connecting";
        OnPropertyChanged(nameof(ShowChatConnectionHint));
        connectOutcome = new TaskCompletionSource<HelperConnectOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await sessionRuntime.StartHelperAsync(code, connectCts.Token);
            // NKN transport logs these stages after JoinRequest Ack to avoid optimistic duplicates.
            if (!string.Equals(transportConfig.Key, "NKN", StringComparison.OrdinalIgnoreCase))
            {
                LogReliability(SessionReliabilityStage.DiscoveryFoundHost);
                LogReliability(SessionReliabilityStage.JoinRequestSent);
            }

            var outcome = await WaitForConnectOutcomeAsync(connectCts.Token);
            if (outcome == HelperConnectOutcome.PendingTimeout)
            {
                LogReliability(SessionReliabilityStage.Disconnected, "approval_timeout", "No response yet.");
                var failure = TransportFailureMapper.CreateTimeout("approval_timeout");
                await sessionRuntime.FailAsync(failure, UserErrorMapper.HelperApprovalTimeout());
                OnPropertyChanged(nameof(ShowChatConnectionHint));
            }
        }
        catch (OperationCanceledException)
        {
            // User navigated away or a new connect attempt replaced this one.
        }
        catch (TimeoutException ex)
        {
            LogReliability(SessionReliabilityStage.DiscoveryTimeout, "timeout", "No one found with that code.");
            var snapshot = NknRuntimeDiagnostics.Snapshot();
            var failure = TransportFailureMapper.FromException(ex, snapshot.LastError, snapshot.LastDisconnectReason);
            await sessionRuntime.FailAsync(failure, UserErrorMapper.FromHelperTimeoutException(ex));
            MarkFailedAttemptNow();
            OnPropertyChanged(nameof(ShowChatConnectionHint));
        }
        catch (Exception ex)
        {
            var (errorCode, errorHint) = GetReliabilityError();
            LogReliability(SessionReliabilityStage.Disconnected, errorCode, errorHint);
            var snapshot = NknRuntimeDiagnostics.Snapshot();
            var uiMessage = UserErrorMapper.IsNknStartFailure(snapshot.LastError)
                ? UserErrorMapper.NknStartFailedReinstall()
                : UserErrorMapper.HelperGenericConnectFailure();
            var failure = TransportFailureMapper.FromException(ex, snapshot.LastError, snapshot.LastDisconnectReason);
            await sessionRuntime.FailAsync(failure, uiMessage);
            MarkFailedAttemptNow();
            OnPropertyChanged(nameof(ShowChatConnectionHint));
        }
        finally
        {
            IsConnecting = false;
            ConnectCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task RetryAsync()
    {
        connectCts?.Cancel();
        connectCts?.Dispose();
        connectCts = null;
        connectOutcome = null;

        await sessionRuntime.ResetAsync();

        await UiThreadDispatch.RunAsync(() =>
        {
            IsConnecting = false;
            StatusText = string.Empty;
            ConnectionState = "Idle";
            ShowChatNotice = false;
            OnPropertyChanged(nameof(ShowMainControls));
            OnPropertyChanged(nameof(ShowConnectAction));
            OnPropertyChanged(nameof(ShowRetryAction));
            OnPropertyChanged(nameof(ShowCopyFeedbackInline));
            ConnectCommand.NotifyCanExecuteChanged();
            RetryCommand.NotifyCanExecuteChanged();
        });
    }

    private async Task SendChatAsync()
    {
        if (disposed)
        {
            return;
        }

        if (!sessionRuntime.CanSendChat && !IsConnecting && SessionCode.TryParse(CodeInput, out _))
        {
            await ConnectAsync();
        }

        var draft = ChatDraft;
        var optimisticText = draft.Trim();
        if (string.IsNullOrWhiteSpace(optimisticText))
        {
            return;
        }

        ChatDraft = string.Empty;
        ShowChatNotice = false;
        var optimisticLine = AddChatLine(optimisticText, isLocal: true);

        var message = await sessionRuntime.TrySendChatTextAsync(draft, CancellationToken.None);
        if (message is null)
        {
            ChatMessages.Remove(optimisticLine);
            OnPropertyChanged(nameof(HasChatMessages));
            OnPropertyChanged(nameof(ShowNoMessagesPlaceholder));
            OnPropertyChanged(nameof(ShowChatPanel));
            OnPropertyChanged(nameof(ShowChatConnectionHint));
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

    private void RequestSendFileWindow()
    {
        SendFileRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OpenDiagnostics()
    {
        openDiagnosticsAction?.Invoke();
    }

    private async Task CopyInstallMessageAsync()
    {
        if (clipboardService is null)
        {
            copyFeedback.Show("Could not copy. Please try again.");
            return;
        }

        try
        {
            var text = BuildHelperInstallMessage();
            await clipboardService.SetTextAsync(text);
            copyFeedback.Show("Copied. Paste it in your chat.");
        }
        catch
        {
            copyFeedback.Show("Could not copy. Please try again.");
        }
    }

    private string BuildHelperInstallMessage()
    {
        const string releasesUrl = "https://github.com/resu12/nLink/releases";
        var url = string.IsNullOrWhiteSpace(shareMessageConfig.DownloadUrl)
            ? releasesUrl
            : shareMessageConfig.DownloadUrl;
        return ShareMessageBuilder.BuildHelperInstallMessage(url);
    }

    private void OnApproved(object? sender, EventArgs e)
    {
        connectOutcome?.TrySetResult(HelperConnectOutcome.Approved);
        _ = UiThreadDispatch.RunAsync(() =>
        {
            StatusText = transportConfig.ApprovedStatusText;
            ConnectionState = "Connected";
            ShowChatNotice = false;
            LogReliability(SessionReliabilityStage.Approved);
            LogReliability(SessionReliabilityStage.Completed);
            OnPropertyChanged(nameof(ShowChatConnectionHint));
        });
    }

    private void OnRejected(object? sender, EventArgs e)
    {
        connectOutcome?.TrySetResult(HelperConnectOutcome.Rejected);
        _ = UiThreadDispatch.RunAsync(() =>
        {
            StatusText = UserErrorMapper.HelperRejected();
            ConnectionState = "Rejected";
            LogReliability(SessionReliabilityStage.Rejected, "rejected", "They did not allow the connection.");
            OnPropertyChanged(nameof(ShowChatConnectionHint));
        });
    }

    private void OnDisconnected(object? sender, EventArgs e)
    {
        if (disposed || connectCts?.IsCancellationRequested == true)
        {
            return;
        }

        connectOutcome?.TrySetResult(HelperConnectOutcome.Disconnected);

        _ = UiThreadDispatch.RunAsync(() =>
        {
            var runtimeStatus = sessionRuntime.StatusText;
            if (string.IsNullOrWhiteSpace(runtimeStatus))
            {
                // Remote session-end can race with runtime reset back to Idle, clearing StatusText
                // before the helper VM handles the disconnect callback. If we were connected and
                // there is no classified failure, prefer the friendly session-ended message.
                var nknSnapshot = NknRuntimeDiagnostics.Snapshot();
                var remoteSessionEndSeenInDiagnostics =
                    string.Equals(nknSnapshot.LastEnvelopeType, "SessionEnd", StringComparison.OrdinalIgnoreCase);
                var disconnectedAfterConnectedNoFailure =
                    string.Equals(ConnectionState, "Connected", StringComparison.Ordinal) &&
                    sessionRuntime.LastTransportFailure is null;

                StatusText = sessionRuntime.LastTransportFailure is null &&
                             (sessionRuntime.LastDisconnectWasRemoteEnd ||
                              disconnectedAfterConnectedNoFailure ||
                              remoteSessionEndSeenInDiagnostics)
                    ? "The other person ended the session."
                    : transportConfig.HelperDisconnectedText;
            }
            else
            {
                StatusText = runtimeStatus;
            }
            var (errorCode, errorHint) = GetReliabilityError();
            LogReliability(SessionReliabilityStage.Disconnected, errorCode, errorHint);
            if (string.Equals(sessionRuntime.StatusText, "Connection lost.", StringComparison.Ordinal))
            {
                ConnectionState = "Failed";
            }
            else if (ConnectionState != "Connected")
            {
                ConnectionState = "Disconnected";
            }
            OnPropertyChanged(nameof(ShowChatConnectionHint));
            OnPropertyChanged(nameof(ShowMainControls));
            OnPropertyChanged(nameof(ShowConnectAction));
            OnPropertyChanged(nameof(ShowRetryAction));
            OnPropertyChanged(nameof(ShowInlineStatusText));
            OnPropertyChanged(nameof(ShowCopyFeedbackInline));
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

    private void OnSessionRuntimeStateChanged(object? sender, SessionRuntimeStateChangedEventArgs e)
    {
        if (disposed)
        {
            return;
        }

        _ = UiThreadDispatch.RunAsync(() =>
        {
            SyncFromRuntime();
        });
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
        OnPropertyChanged(nameof(ShowChatPanel));
        OnPropertyChanged(nameof(ShowChatConnectionHint));
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

        return (lastError, "Connection did not complete. Try again or use a new code.");
    }

    private void SyncFromRuntime()
    {
        switch (sessionRuntime.State)
        {
            case SessionRuntimeState.Connecting:
                ClearFailurePresentation();
                StatusText = "Connecting…";
                ConnectionState = "Connecting";
                break;
            case SessionRuntimeState.Connected:
                ClearFailurePresentation();
                StatusText = transportConfig.ApprovedStatusText;
                ConnectionState = "Connected";
                ShowChatNotice = false;
                break;
            case SessionRuntimeState.Rejected:
                ClearFailurePresentation();
                StatusText = UserErrorMapper.HelperRejected();
                ConnectionState = "Rejected";
                break;
            case SessionRuntimeState.Failed:
                StatusText = string.IsNullOrWhiteSpace(sessionRuntime.StatusText)
                    ? UserErrorMapper.HelperGenericConnectFailure()
                    : sessionRuntime.StatusText;
                ConnectionState = "Failed";
                ApplyFailurePresentation();
                break;
            case SessionRuntimeState.Disconnected:
                StatusText = string.IsNullOrWhiteSpace(sessionRuntime.StatusText)
                    ? "Connection lost."
                    : sessionRuntime.StatusText;
                ConnectionState = "Failed";
                ApplyFailurePresentation();
                break;
        }

        OnPropertyChanged(nameof(ShowChatConnectionHint));
        OnPropertyChanged(nameof(IsChatReady));
        OnPropertyChanged(nameof(ShowConnectAction));
        OnPropertyChanged(nameof(ShowRetryAction));
        OnPropertyChanged(nameof(ShowOpenDiagnosticsLink));
        OnPropertyChanged(nameof(ShowInlineStatusText));
        OnPropertyChanged(nameof(ShowFailurePanel));
        OnPropertyChanged(nameof(ShowMainControls));
        OnPropertyChanged(nameof(ShowCopyFeedbackInline));
        SendChatCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        OpenDiagnosticsCommand.NotifyCanExecuteChanged();
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

    private bool IsInFailureCooldown()
    {
        return nowProvider() - lastFailedAttemptUtc < connectFailureCooldown;
    }

    private void MarkFailedAttemptNow()
    {
        lastFailedAttemptUtc = nowProvider();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(connectFailureCooldown);
            }
            catch
            {
                return;
            }

            if (disposed)
            {
                return;
            }

            await UiThreadDispatch.RunAsync(() => ConnectCommand.NotifyCanExecuteChanged());
        });
    }

    private async Task<HelperConnectOutcome> WaitForConnectOutcomeAsync(CancellationToken ct)
    {
        var pending = connectOutcome;
        if (pending is null)
        {
            return HelperConnectOutcome.None;
        }

        try
        {
            return await pending.Task.WaitAsync(approvalTimeout, ct);
        }
        catch (TimeoutException)
        {
            return HelperConnectOutcome.PendingTimeout;
        }
        catch (OperationCanceledException)
        {
            return HelperConnectOutcome.None;
        }
    }

    private void OnCopyFeedbackPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(InlineTransientText.IsVisible) or nameof(InlineTransientText.Text))
        {
            OnPropertyChanged(nameof(ShowCopyFeedbackInline));
        }
    }

    private enum HelperConnectOutcome
    {
        None,
        Approved,
        Rejected,
        Disconnected,
        PendingTimeout,
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
            StatusText = UserErrorMapper.NknStartFailedReinstall();
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
}
