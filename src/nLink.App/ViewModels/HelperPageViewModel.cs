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
    private readonly IClipboardService? clipboardService;
    private readonly ShareMessageConfig shareMessageConfig;

    private string codeInput = string.Empty;
    private string statusText = string.Empty;
    private string connectionState = "Idle";
    private string chatDraft = string.Empty;
    private bool isConnecting;
    private bool showChatNotice;
    private bool showCopyFeedback;
    private string copyFeedbackText = string.Empty;
    private bool startupBlocked;
    private CancellationTokenSource? connectCts;
    private CancellationTokenSource? copyFeedbackResetCts;
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
        TimeSpan? approvalTimeout = null,
        TimeSpan? connectFailureCooldown = null,
        Func<DateTimeOffset>? nowProvider = null)
    {
        this.cancelAction = cancelAction;
        this.openDiagnosticsAction = openDiagnosticsAction;
        this.transportConfig = transportConfig;
        this.sessionRuntime = sessionRuntime;
        this.clipboardService = clipboardService;
        this.shareMessageConfig = shareMessageConfig ?? new ShareMessageConfig(null);
        this.approvalTimeout = approvalTimeout ?? DefaultApprovalTimeout;
        this.connectFailureCooldown = connectFailureCooldown ?? DefaultConnectFailureCooldown;
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);

        ChatMessages = new ObservableCollection<ChatLineViewModel>();

        sessionRuntime.StateChanged += OnSessionRuntimeStateChanged;
        sessionRuntime.Approved += OnApproved;
        sessionRuntime.Rejected += OnRejected;
        sessionRuntime.Disconnected += OnDisconnected;
        sessionRuntime.ChatMessageReceived += OnChatMessageReceived;
        sessionRuntime.ChatMessageReceivedBeforeApproved += OnChatMessageReceivedBeforeApproved;
        sessionRuntime.ChatStateChanged += OnChatStateChanged;

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, CanConnect);
        CopyInstallMessageCommand = new AsyncRelayCommand(CopyInstallMessageAsync);
        SendFileCommand = new RelayCommand(RequestSendFileWindow);
        SendChatCommand = new AsyncRelayCommand(SendChatAsync, CanSendChat);
        RetryCommand = new AsyncRelayCommand(RetryAsync, CanRetry);
        OpenDiagnosticsCommand = new RelayCommand(OpenDiagnostics, CanOpenDiagnostics);
        CancelCommand = new RelayCommand(CancelAndGoBack);

        InitializeStartupAvailabilityState();
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
                OnPropertyChanged(nameof(ShowInlineStatusText));
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
    public bool ShowInlineStatusText => ShowStatusText && !IsStartupBlocked && !IsConnectedView;

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
                OnPropertyChanged(nameof(ShowInlineStatusText));
                OnPropertyChanged(nameof(ShowCopyFeedbackInline));
                ConnectCommand.NotifyCanExecuteChanged();
                RetryCommand.NotifyCanExecuteChanged();
                OpenDiagnosticsCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool ShowMainControls => !IsStartupBlocked && !ShowRetryAction && !IsConnectedView;

    public bool ShowConnectAction => ShowMainControls && !ShowRetryAction;

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

    public IRelayCommand OpenDiagnosticsCommand { get; }

    public IRelayCommand CancelCommand { get; }
    public IRelayCommand EndSessionCommand => CancelCommand;

    public event EventHandler? SendFileRequested;

    public bool ShowRetryAction => !IsStartupBlocked &&
                                   string.Equals(ConnectionState, "Failed", StringComparison.Ordinal) &&
                                   string.Equals(StatusText, "Connection lost.", StringComparison.Ordinal);

    public bool ShowOpenDiagnosticsLink =>
        openDiagnosticsAction is not null &&
        (IsStartupBlocked || string.Equals(StatusText, UserErrorMapper.NknStartFailedReinstall(), StringComparison.Ordinal));

    public bool ShowCopyFeedback
    {
        get => showCopyFeedback;
        private set
        {
            if (SetProperty(ref showCopyFeedback, value))
            {
                OnPropertyChanged(nameof(ShowCopyFeedbackInline));
            }
        }
    }

    public bool ShowCopyFeedbackInline => ShowMainControls && ShowCopyFeedback;

    public bool ShowBackButton => !IsConnectedView;

    public string CopyFeedbackText
    {
        get => copyFeedbackText;
        private set => SetProperty(ref copyFeedbackText, value);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        sessionRuntime.StateChanged -= OnSessionRuntimeStateChanged;
        sessionRuntime.Approved -= OnApproved;
        sessionRuntime.Rejected -= OnRejected;
        sessionRuntime.Disconnected -= OnDisconnected;
        sessionRuntime.ChatMessageReceived -= OnChatMessageReceived;
        sessionRuntime.ChatMessageReceivedBeforeApproved -= OnChatMessageReceivedBeforeApproved;
        sessionRuntime.ChatStateChanged -= OnChatStateChanged;
        sessionRuntime.SetReliabilityAttempt(null);
        _ = sessionRuntime.ResetAsync();

        connectCts?.Cancel();
        connectCts?.Dispose();
        connectCts = null;
        copyFeedbackResetCts?.Cancel();
        copyFeedbackResetCts?.Dispose();
        copyFeedbackResetCts = null;
    }

    private bool CanConnect()
    {
        return !IsStartupBlocked && !IsConnecting && SessionCode.TryParse(CodeInput, out _);
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
                await sessionRuntime.FailAsync(UserErrorMapper.HelperApprovalTimeout());
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
            await sessionRuntime.FailAsync(UserErrorMapper.FromHelperTimeoutException(ex));
            MarkFailedAttemptNow();
            OnPropertyChanged(nameof(ShowChatConnectionHint));
        }
        catch (Exception)
        {
            var (errorCode, errorHint) = GetReliabilityError();
            LogReliability(SessionReliabilityStage.Disconnected, errorCode, errorHint);
            var uiMessage = UserErrorMapper.IsNknStartFailure(NknRuntimeDiagnostics.Snapshot().LastError)
                ? UserErrorMapper.NknStartFailedReinstall()
                : UserErrorMapper.HelperGenericConnectFailure();
            await sessionRuntime.FailAsync(uiMessage);
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

        var message = await sessionRuntime.TrySendChatTextAsync(ChatDraft, CancellationToken.None);
        if (message is null)
        {
            return;
        }

        ChatDraft = string.Empty;
        ShowChatNotice = false;
        AddChatLine(message.Value.Text, isLocal: true);
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
            _ = ShowTransientCopyFeedbackAsync("Could not copy. Please try again.");
            return;
        }

        try
        {
            var text = BuildHelperInstallMessage();
            await clipboardService.SetTextAsync(text);
            _ = ShowTransientCopyFeedbackAsync("Copied. Paste it in your chat.");
        }
        catch
        {
            _ = ShowTransientCopyFeedbackAsync("Could not copy. Please try again.");
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
            StatusText = string.Equals(sessionRuntime.StatusText, "Connection lost.", StringComparison.Ordinal)
                ? "Connection lost."
                : transportConfig.HelperDisconnectedText;
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
        OnPropertyChanged(nameof(ShowChatPanel));
        OnPropertyChanged(nameof(ShowChatConnectionHint));
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
                StatusText = "Connecting…";
                ConnectionState = "Connecting";
                break;
            case SessionRuntimeState.Connected:
                StatusText = transportConfig.ApprovedStatusText;
                ConnectionState = "Connected";
                ShowChatNotice = false;
                break;
            case SessionRuntimeState.Rejected:
                StatusText = UserErrorMapper.HelperRejected();
                ConnectionState = "Rejected";
                break;
            case SessionRuntimeState.Failed:
                StatusText = string.IsNullOrWhiteSpace(sessionRuntime.StatusText)
                    ? UserErrorMapper.HelperGenericConnectFailure()
                    : sessionRuntime.StatusText;
                ConnectionState = "Failed";
                break;
            case SessionRuntimeState.Disconnected:
                StatusText = string.IsNullOrWhiteSpace(sessionRuntime.StatusText)
                    ? "Connection lost."
                    : sessionRuntime.StatusText;
                ConnectionState = "Failed";
                break;
        }

        OnPropertyChanged(nameof(ShowChatConnectionHint));
        OnPropertyChanged(nameof(IsChatReady));
        OnPropertyChanged(nameof(ShowConnectAction));
        OnPropertyChanged(nameof(ShowRetryAction));
        OnPropertyChanged(nameof(ShowOpenDiagnosticsLink));
        OnPropertyChanged(nameof(ShowInlineStatusText));
        OnPropertyChanged(nameof(ShowMainControls));
        OnPropertyChanged(nameof(ShowCopyFeedbackInline));
        SendChatCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        OpenDiagnosticsCommand.NotifyCanExecuteChanged();
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

    private enum HelperConnectOutcome
    {
        None,
        Approved,
        Rejected,
        Disconnected,
        PendingTimeout,
    }

    private async Task ShowTransientCopyFeedbackAsync(string text)
    {
        copyFeedbackResetCts?.Cancel();
        copyFeedbackResetCts?.Dispose();
        copyFeedbackResetCts = new CancellationTokenSource();
        var ct = copyFeedbackResetCts.Token;

        await UiThreadDispatch.RunAsync(() =>
        {
            CopyFeedbackText = text;
            ShowCopyFeedback = true;
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
            ShowCopyFeedback = false;
            CopyFeedbackText = string.Empty;
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
            StatusText = UserErrorMapper.NknStartFailedReinstall();
            ConnectionState = "Failed";
        }
    }
}
