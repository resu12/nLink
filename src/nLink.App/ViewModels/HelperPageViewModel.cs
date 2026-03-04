using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
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
    private static readonly TimeSpan RecoveryTransientThrottle = TimeSpan.FromSeconds(2);
    private static readonly Regex AttemptLabelRegex = new(@"\s*\(?attempt\s+\d+(?:,\s*next retry in \d+s)?\)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Action cancelAction;
    private readonly Action backAction;
    private readonly Action? openDiagnosticsAction;
    private readonly TransportRuntimeConfig transportConfig;
    private readonly SessionRuntime sessionRuntime;
    private readonly StatusPresenter statusPresenter;
    private readonly bool ownsStatusPresenter;
    private readonly IClipboardService? clipboardService;
    private readonly ShareMessageConfig shareMessageConfig;
    private readonly SessionUiStateStore? uiStateStore;

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
    private bool hasUiRecoveryTransient;
    private string uiRecoveryTransientText = string.Empty;
    private bool uiRecoveryTransientCanCancel;
    private DateTimeOffset nextUiRecoveryBannerAllowedAt = DateTimeOffset.MinValue;
    private string uiRecoveryTransientKey = string.Empty;
    private bool uiRecoveryTransientDismissed;
    private bool isConnecting;
    private bool showChatNotice;
    private bool startupBlocked;
    private UserFacingStatus bannerStatus = UserFacingStatus.IdleStatus;
    private UserFacingStatus presenterBannerStatus = UserFacingStatus.IdleStatus;
    private bool showStatusBanner;
    private string? statusBannerDetailsText;
    private bool canStartOrConnect = true;
    private bool canEndSession = true;
    private bool canOpenDiagnostics;
    private bool canSendFiles;
    private bool isChatInputEnabled;
    private SessionUiPhase effectivePhase;
    private bool endInvoked;
    private bool wasConnected;
    private SessionEndReason? endReason;
    private bool endSessionRequested;
    private bool endSessionCancelInvoked;
    private CancellationTokenSource? connectCts;
    private readonly InlineTransientText copyFeedback = new();
    private readonly Func<DateTimeOffset> nowProvider;
    private readonly TimeSpan connectFailureCooldown;
    private readonly TimeSpan approvalTimeout;
    private DateTimeOffset lastFailedAttemptUtc = DateTimeOffset.MinValue;
    private TaskCompletionSource<HelperConnectOutcome>? connectOutcome;
    private SessionReliabilityAttempt? reliabilityAttempt;
    private SessionUiPhase lastObservedUiPhase;
    private SessionUiPhase fallbackUiPhase;
    private bool lastKnownShowRemoteScreenShareFrame;
    private bool lastKnownShowHelperMainContent = true;
    private string lastKnownHeaderStatusText = "Ready";
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
        Func<DateTimeOffset>? nowProvider = null,
        SessionUiStateStore? uiStateStore = null,
        Action? backAction = null)
    {
        this.cancelAction = cancelAction;
        this.backAction = backAction ?? cancelAction;
        this.openDiagnosticsAction = openDiagnosticsAction;
        this.transportConfig = transportConfig;
        this.sessionRuntime = sessionRuntime;
        this.statusPresenter = statusPresenter ?? new StatusPresenter(sessionRuntime);
        ownsStatusPresenter = statusPresenter is null;
        this.clipboardService = clipboardService;
        this.shareMessageConfig = shareMessageConfig ?? new ShareMessageConfig(null);
        this.uiStateStore = uiStateStore;
        this.approvalTimeout = approvalTimeout ?? DefaultApprovalTimeout;
        this.connectFailureCooldown = connectFailureCooldown ?? DefaultConnectFailureCooldown;
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
        lastObservedUiPhase = uiStateStore?.Phase ?? SessionUiPhase.Idle;
        fallbackUiPhase = lastObservedUiPhase;
        ScreenShareViewer = new ScreenShareViewerViewModel();
        lastKnownShowRemoteScreenShareFrame = ShowRemoteScreenShareFrame;
        lastKnownShowHelperMainContent = ShowHelperMainContent;
        lastKnownHeaderStatusText = HeaderStatusText;

        ChatMessages = new ObservableCollection<ChatLineViewModel>();

        sessionRuntime.StateChanged += OnSessionRuntimeStateChanged;
        sessionRuntime.TransientStatusChanged += OnTransientStatusChanged;
        sessionRuntime.Approved += OnApproved;
        sessionRuntime.Rejected += OnRejected;
        sessionRuntime.Disconnected += OnDisconnected;
        sessionRuntime.ScreenShareFrameCompleted += OnScreenShareFrameCompleted;
        sessionRuntime.ScreenShareStopped += OnScreenShareStopped;
        sessionRuntime.ChatMessageReceived += OnChatMessageReceived;
        sessionRuntime.ChatMessageReceivedBeforeApproved += OnChatMessageReceivedBeforeApproved;
        sessionRuntime.ChatStateChanged += OnChatStateChanged;
        this.statusPresenter.StatusChanged += OnStatusPresenterChanged;
        copyFeedback.PropertyChanged += OnCopyFeedbackPropertyChanged;
        ScreenShareViewer.PropertyChanged += OnScreenShareViewerPropertyChanged;
        if (this.uiStateStore is not null)
        {
            this.uiStateStore.PropertyChanged += OnUiStateStorePropertyChanged;
        }

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, CanConnect);
        CopyInstallMessageCommand = new AsyncRelayCommand(CopyInstallMessageAsync);
        SendFileCommand = new RelayCommand(RequestSendFileWindow);
        SendChatCommand = new AsyncRelayCommand(SendChatAsync, CanSendChat);
        RetryCommand = new AsyncRelayCommand(RetryAsync, CanRetry);
        CancelTransientCommand = new AsyncRelayCommand(CancelTransientAsync, CanCancelTransientOperation);
        OpenDiagnosticsCommand = new RelayCommand(OpenDiagnostics, CanOpenDiagnosticsCommand);
        CancelCommand = new RelayCommand(CancelAndGoBack);
        EndSessionCommand = new RelayCommand(EndSession, CanTriggerEndSession);

        InitializeStartupAvailabilityState();
        presenterBannerStatus = NormalizeStatusForDisplay(this.statusPresenter.CurrentStatus);
        BannerStatus = presenterBannerStatus;
        SyncTransientStatusFromRuntime();
        if (this.uiStateStore is not null && this.uiStateStore.Phase == SessionUiPhase.Idle)
        {
            this.uiStateStore.SetPhase(SessionUiPhase.Waiting, "Constructor:HelperSeed");
        }
        ApplySessionBannerPolicy();
        UpdateUiFromSnapshot();
    }

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
                OnPropertyChanged(nameof(HeaderStatusText));
                OnPropertyChanged(nameof(ChatConnectionPillText));
                OnPropertyChanged(nameof(ShowChatConnectionPill));
                OnPropertyChanged(nameof(ShowTransientStatusPanel));
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
                OnPropertyChanged(nameof(HeaderStatusText));
                OnPropertyChanged(nameof(ChatConnectionPillText));
                OnPropertyChanged(nameof(ShowChatConnectionPill));
                OnPropertyChanged(nameof(ShowTransientStatusPanel));
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
        private set => SetProperty(ref bannerStatus, value);
    }

    public bool ShowStatusBanner
    {
        get => showStatusBanner;
        private set => SetProperty(ref showStatusBanner, value);
    }

    public string? StatusBannerDetailsText
    {
        get => statusBannerDetailsText;
        private set => SetProperty(ref statusBannerDetailsText, value);
    }

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
        private set
        {
            if (SetProperty(ref failureTitle, value))
            {
                OnPropertyChanged(nameof(HeaderStatusText));
                OnPropertyChanged(nameof(ShowChatConnectionPill));
                OnPropertyChanged(nameof(ShowTransientStatusPanel));
            }
        }
    }

    public string FailureMessage
    {
        get => failureMessage;
        private set
        {
            if (SetProperty(ref failureMessage, value))
            {
                OnPropertyChanged(nameof(HeaderStatusText));
                OnPropertyChanged(nameof(ShowChatConnectionPill));
                OnPropertyChanged(nameof(ShowTransientStatusPanel));
            }
        }
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

    public string ChatConnectionPillText =>
        EffectivePhase switch
        {
            SessionUiPhase.Connected => "Connected",
            SessionUiPhase.Connecting => "Connecting…",
            SessionUiPhase.Recovering => "Reconnecting…",
            _ => "Not connected",
        };

    public bool ShowChatConnectionPill => !HeaderStatusText.StartsWith(ChatConnectionPillText, StringComparison.Ordinal);

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

    public string HeaderStatusText => AppendScreenShareSuffix(
        EffectivePhase switch
        {
            SessionUiPhase.Connecting => "Connecting…",
            SessionUiPhase.Recovering => "Reconnecting…",
            SessionUiPhase.Connected => "Connected",
            SessionUiPhase.Failed => string.IsNullOrWhiteSpace(FailureTitle) ? "Connection failed" : FailureTitle,
            SessionUiPhase.Ended => !string.IsNullOrWhiteSpace(StatusText)
                ? StatusText
                : !string.IsNullOrWhiteSpace(FailureTitle)
                    ? FailureTitle
                    : "Session ended",
            _ => !string.IsNullOrWhiteSpace(StatusText) ? StatusText : "Ready",
        });

    public SessionUiPhase EffectivePhase
    {
        get => effectivePhase;
        private set
        {
            if (SetProperty(ref effectivePhase, value))
            {
                OnPropertyChanged(nameof(HeaderStatusText));
                OnPropertyChanged(nameof(ChatConnectionPillText));
                OnPropertyChanged(nameof(ShowChatConnectionPill));
                OnPropertyChanged(nameof(ShowTransientStatusPanel));
            }
        }
    }

    public ScreenShareViewerViewModel ScreenShareViewer { get; }

    public bool IsChatReady => sessionRuntime.CanSendChat;

    public Bitmap? RemoteScreenShareFrame => ScreenShareViewer.CurrentFrame as Bitmap;

    public bool ShowRemoteScreenShareFrame =>
        ScreenShareViewer.IsActive &&
        RemoteScreenShareFrame is not null;

    public bool ShowDefaultScreenSharePlaceholder => !ShowRemoteScreenShareFrame;

    public bool ShowScreenShareViewerError =>
        ScreenShareViewer.IsActive &&
        RemoteScreenShareFrame is null &&
        !string.IsNullOrWhiteSpace(ScreenShareViewerMessage);

    public string ScreenShareViewerMessage => BuildScreenShareViewerMessage(ScreenShareViewer.StatusText);

    public bool ShowHelperMainContent => !ShowRemoteScreenShareFrame;

    public bool CanStartOrConnect
    {
        get => canStartOrConnect;
        private set => SetProperty(ref canStartOrConnect, value);
    }

    public bool CanEndSession
    {
        get => canEndSession;
        private set => SetProperty(ref canEndSession, value);
    }

    public bool CanOpenDiagnostics
    {
        get => canOpenDiagnostics;
        private set => SetProperty(ref canOpenDiagnostics, value);
    }

    public bool CanSendFiles
    {
        get => canSendFiles;
        private set => SetProperty(ref canSendFiles, value);
    }

    public bool IsChatInputEnabled
    {
        get => isChatInputEnabled;
        private set => SetProperty(ref isChatInputEnabled, value);
    }

    public IAsyncRelayCommand ConnectCommand { get; }

    public IAsyncRelayCommand CopyInstallMessageCommand { get; }

    public IRelayCommand SendFileCommand { get; }

    public IAsyncRelayCommand SendChatCommand { get; }

    public IAsyncRelayCommand RetryCommand { get; }
    public IAsyncRelayCommand CancelTransientCommand { get; }

    public IRelayCommand OpenDiagnosticsCommand { get; }

    public IRelayCommand CancelCommand { get; }
    public IRelayCommand EndSessionCommand { get; }
    public IRelayCommand StatusBannerCopyDiagnosticsCommand => OpenDiagnosticsCommand;
    public IAsyncRelayCommand StatusBannerCancelCommand => CancelTransientCommand;

    public event EventHandler? SendFileRequested;

    public bool ShowRetryAction => !IsStartupBlocked &&
                                   string.Equals(ConnectionState, "Failed", StringComparison.Ordinal) &&
                                   string.Equals(StatusText, "Connection lost.", StringComparison.Ordinal);

    public bool ShowOpenDiagnosticsLink => CanOpenDiagnostics;

    public InlineTransientText CopyFeedback => copyFeedback;
    public bool ShowCopyFeedbackInline => ShowMainControls && copyFeedback.IsVisible;

    public bool ShowBackButton => !IsConnectedView;
    public bool ShowTransientBanner
    {
        get => showTransientBanner;
        private set
        {
            if (SetProperty(ref showTransientBanner, value))
            {
                OnPropertyChanged(nameof(ShowTransientStatusPanel));
            }
        }
    }

    public string TransientBannerText
    {
        get => transientBannerText;
        private set
        {
            if (SetProperty(ref transientBannerText, value))
            {
                OnPropertyChanged(nameof(ShowTransientStatusPanel));
            }
        }
    }

    public bool ShowTransientStatusPanel => ShowTransientBanner && !IsTransientBannerDuplicateWithHeader();

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
        sessionRuntime.ScreenShareFrameCompleted -= OnScreenShareFrameCompleted;
        sessionRuntime.ScreenShareStopped -= OnScreenShareStopped;
        sessionRuntime.ChatMessageReceived -= OnChatMessageReceived;
        sessionRuntime.ChatMessageReceivedBeforeApproved -= OnChatMessageReceivedBeforeApproved;
        sessionRuntime.ChatStateChanged -= OnChatStateChanged;
        statusPresenter.StatusChanged -= OnStatusPresenterChanged;
        copyFeedback.PropertyChanged -= OnCopyFeedbackPropertyChanged;
        ScreenShareViewer.PropertyChanged -= OnScreenShareViewerPropertyChanged;
        if (uiStateStore is not null)
        {
            uiStateStore.PropertyChanged -= OnUiStateStorePropertyChanged;
        }
        if (ownsStatusPresenter)
        {
            statusPresenter.Dispose();
        }
        sessionRuntime.SetReliabilityAttempt(null);
        sessionRuntime.ResetAsync().GetAwaiter().GetResult();

        connectCts?.Cancel();
        connectCts?.Dispose();
        connectCts = null;
        ScreenShareViewer.Dispose();
        copyFeedback.Dispose();
    }

    private bool CanConnect()
    {
        return CanStartOrConnect && !IsStartupBlocked && !IsConnecting && SessionCode.TryParse(CodeInput, out _);
    }

    private bool CanCancelTransientOperation()
    {
        return ShowTransientBanner && CanCancelTransient;
    }

    private async Task CancelTransientAsync()
    {
        uiRecoveryTransientDismissed = true;
        ClearUiRecoveryTransient();
        ShowTransientBanner = false;
        TransientBannerText = string.Empty;
        CanCancelTransient = false;
        if (IsConnecting ||
            string.Equals(ConnectionState, "Connecting", StringComparison.Ordinal) ||
            sessionRuntime.State == SessionRuntimeState.Connecting)
        {
            try
            {
                await sessionRuntime.DisconnectAsync();
            }
            catch
            {
                // best effort UX action
            }
            uiStateStore?.SetPhase(SessionUiPhase.Waiting, "Transient:CancelledWhileConnecting");
            ApplySessionBannerPolicy();
            await RetryAsync();
            return;
        }

        if (uiStateStore?.Phase == SessionUiPhase.Recovering)
        {
            uiStateStore.SetPhase(SessionUiPhase.Failed, "Transient:CancelledByUser");
            ApplySessionBannerPolicy();
        }

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
        return IsChatInputEnabled && !string.IsNullOrWhiteSpace(ChatDraft) && sessionRuntime.CanSendChat;
    }

    private bool CanRetry()
    {
        if (IsStartupBlocked)
        {
            return false;
        }

        var isRetryState = string.Equals(ConnectionState, "Failed", StringComparison.Ordinal) ||
            string.Equals(ConnectionState, "Disconnected", StringComparison.Ordinal) ||
            string.Equals(ConnectionState, "Rejected", StringComparison.Ordinal);
        return (CanStartOrConnect || isRetryState) && (!IsConnecting || isRetryState);
    }

    private bool CanTriggerEndSession()
    {
        return CanEndSession && !endInvoked;
    }

    private bool CanOpenDiagnosticsCommand()
    {
        return CanOpenDiagnostics;
    }

    private async Task ConnectAsync()
    {
        PrepareForNewSession();
        uiRecoveryTransientDismissed = false;

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
        PrepareForNewSession();

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

        try
        {
            var message = await sessionRuntime.TrySendChatTextAsync(draft, CancellationToken.None);
            if (message is not null)
            {
                return;
            }
        }
        catch
        {
            // Keep the session alive on chat send failure; the user can retry the draft.
        }

        RemoveChatLine(optimisticLine);
        if (string.IsNullOrWhiteSpace(ChatDraft))
        {
            ChatDraft = draft;
        }
    }

    private void CancelAndGoBack()
    {
        ClearRemoteScreenShareFrame();
        backAction();
    }

    private void EndSession()
    {
        if (endInvoked)
        {
            return;
        }

        endInvoked = true;
        EndSessionCommand.NotifyCanExecuteChanged();
        endSessionRequested = true;
        endReason = SessionEndReason.UserEnded;
        uiRecoveryTransientDismissed = true;
        ClearUiRecoveryTransient();
        ShowTransientBanner = false;
        TransientBannerText = string.Empty;
        CanCancelTransient = false;
        uiStateStore?.SetPhase(SessionUiPhase.Ended, "UserEndSession");
        EffectivePhase = SessionUiPhase.Ended;
        IsChatInputEnabled = false;
        CanSendFiles = false;
        CanEndSession = false;
        SendChatCommand.NotifyCanExecuteChanged();
        EndSessionCommand.NotifyCanExecuteChanged();
        AssertUiConsistency();
        ClearRemoteScreenShareFrame();

        if (endSessionCancelInvoked)
        {
            return;
        }

        endSessionCancelInvoked = true;
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
        wasConnected = true;
        endReason = null;
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
            IsConnecting = false;
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
            ClearRemoteScreenShareFrame();
            IsConnecting = false;
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

    private void OnScreenShareFrameCompleted(object? sender, ScreenShareFrameCompletedEventArgs e)
    {
        ScreenShareViewer.OnJpegFrame(e.EncodedFrameBytes);
    }

    private void OnScreenShareStopped(object? sender, EventArgs e)
    {
        if (disposed)
        {
            return;
        }

        _ = UiThreadDispatch.RunAsync(() =>
        {
            ClearRemoteScreenShareFrame();
        });
    }

    private void OnScreenShareViewerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ScreenShareViewerViewModel.CurrentFrame) or
            nameof(ScreenShareViewerViewModel.IsActive) or
            nameof(ScreenShareViewerViewModel.StatusText))
        {
            var previousShowRemoteScreenShareFrame = lastKnownShowRemoteScreenShareFrame;
            var previousShowHelperMainContent = lastKnownShowHelperMainContent;
            var previousHeaderStatusText = lastKnownHeaderStatusText;
            var previousShowViewerError = ShowScreenShareViewerError;
            var previousViewerMessage = ScreenShareViewerMessage;

            OnPropertyChanged(nameof(RemoteScreenShareFrame));

            var nextShowRemoteScreenShareFrame = ShowRemoteScreenShareFrame;
            if (previousShowRemoteScreenShareFrame != nextShowRemoteScreenShareFrame)
            {
                OnPropertyChanged(nameof(ShowRemoteScreenShareFrame));
                lastKnownShowRemoteScreenShareFrame = nextShowRemoteScreenShareFrame;
            }

            var nextShowHelperMainContent = ShowHelperMainContent;
            if (previousShowHelperMainContent != nextShowHelperMainContent)
            {
                OnPropertyChanged(nameof(ShowHelperMainContent));
                lastKnownShowHelperMainContent = nextShowHelperMainContent;
            }

            if (previousShowViewerError != ShowScreenShareViewerError)
            {
                OnPropertyChanged(nameof(ShowScreenShareViewerError));
            }

            if (!string.Equals(previousViewerMessage, ScreenShareViewerMessage, StringComparison.Ordinal))
            {
                OnPropertyChanged(nameof(ScreenShareViewerMessage));
            }

            var nextHeaderStatusText = HeaderStatusText;
            if (!string.Equals(previousHeaderStatusText, nextHeaderStatusText, StringComparison.Ordinal))
            {
                OnPropertyChanged(nameof(HeaderStatusText));
                lastKnownHeaderStatusText = nextHeaderStatusText;
                OnPropertyChanged(nameof(ShowChatConnectionPill));
                OnPropertyChanged(nameof(ShowTransientStatusPanel));
            }
        }
    }

    private void OnTransientStatusChanged(object? sender, SessionRuntimeTransientStatusChangedEventArgs e)
    {
        if (disposed)
        {
            return;
        }

        _ = UiThreadDispatch.RunAsync(SyncTransientStatusFromRuntime);
    }

    private void OnUiStateStorePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (disposed || uiStateStore is null || e.PropertyName != nameof(SessionUiStateStore.Phase))
        {
            return;
        }

        var nextPhase = uiStateStore.Phase;
        var previousPhase = lastObservedUiPhase;
        lastObservedUiPhase = nextPhase;

        _ = UiThreadDispatch.RunAsync(() =>
        {
            SessionUiDebug.LogPhaseChange(
                nameof(HelperPageViewModel),
                previousPhase,
                nextPhase,
                sessionRuntime.State);
            UpdateUiFromSnapshot();
        });
    }

    private void OnStatusPresenterChanged(object? sender, UserFacingStatusChangedEventArgs e)
    {
        if (disposed)
        {
            return;
        }

        _ = UiThreadDispatch.RunAsync(() =>
        {
            presenterBannerStatus = NormalizeStatusForDisplay(e.Status);
            BannerStatus = presenterBannerStatus;
            var p = SessionUxPhaseMapper.FromBannerStatus(e.Status);
            if (p is SessionUiPhase bannerPhase)
            {
                uiStateStore?.SetPhase(bannerPhase, $"StatusPresenter:{e.Status}");
            }

            if (e.Status.Kind == UserStatusKind.Reconnecting)
            {
                TryShowUiRecoveryTransient(
                    "status:reconnecting",
                    string.IsNullOrWhiteSpace(e.Status.Message) ? "Connection lost. Reconnecting…" : SanitizeTransientText(e.Status.Message),
                    canCancel: e.Status.CanCancel || sessionRuntime.CanCancelTransientStatus);
            }
            else if (e.Status.Kind == UserStatusKind.Failed &&
                     p != SessionUiPhase.Ended &&
                     sessionRuntime.State is SessionRuntimeState.Failed or SessionRuntimeState.Disconnected)
            {
                TryShowUiRecoveryTransient(
                    "status:failed",
                    BuildRecoveryTransientText(isRecovering: false),
                    canCancel: false);
            }
            else if (e.Status.Kind == UserStatusKind.Connected)
            {
                uiRecoveryTransientDismissed = false;
                ClearUiRecoveryTransient();
            }

            ApplySessionBannerPolicy();
            NotifyStatusBannerDetailChanged();
            SyncTransientStatusFromRuntime();
        });
    }

    private ChatLineViewModel AddChatLine(string text, bool isLocal)
    {
        var line = new ChatLineViewModel
        {
            Text = text,
            IsLocal = isLocal,
        };
        if (FeatureFlags.EnableChatHardening)
        {
            AddChatLine(line);
            return line;
        }

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

    private void AddChatLine(ChatLineViewModel line)
    {
        // Hardened chat insertion appends exactly once in arrival order.
        ChatMessages.Add(line);

        while (ChatMessages.Count > 100)
        {
            ChatMessages.RemoveAt(0);
        }

        OnPropertyChanged(nameof(HasChatMessages));
        OnPropertyChanged(nameof(ShowNoMessagesPlaceholder));
        OnPropertyChanged(nameof(ShowChatPanel));
        OnPropertyChanged(nameof(ShowChatConnectionHint));
    }

    private void RemoveChatLine(ChatLineViewModel line)
    {
        ChatMessages.Remove(line);
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
        var runtimeTerminalFailure = false;
        if (endSessionRequested)
        {
            ApplyEndReasonPresentation(SessionEndReason.UserEnded);
        }
        else
        {
            switch (sessionRuntime.State)
            {
                case SessionRuntimeState.Connecting:
                    endReason = null;
                    ClearFailurePresentation();
                    StatusText = "Connecting…";
                    ConnectionState = "Connecting";
                    break;
                case SessionRuntimeState.Connected:
                    wasConnected = true;
                    endReason = null;
                    ClearFailurePresentation();
                    StatusText = transportConfig.ApprovedStatusText;
                    ConnectionState = "Connected";
                    ShowChatNotice = false;
                    break;
                case SessionRuntimeState.Rejected:
                    IsConnecting = false;
                    if (endInvoked)
                    {
                        break;
                    }

                    endReason = SessionEndReason.Failed;
                    EnsureFailurePresentation(
                        "Request rejected",
                        "The other side declined the session.",
                        "Start new session");
                    StatusText = string.IsNullOrWhiteSpace(sessionRuntime.StatusText)
                        ? UserErrorMapper.HelperRejected()
                        : sessionRuntime.StatusText;
                    ConnectionState = "Rejected";
                    runtimeTerminalFailure = true;
                    break;
                case SessionRuntimeState.Failed:
                    IsConnecting = false;
                    if (endInvoked)
                    {
                        break;
                    }

                    endReason = ClassifyEndReasonForRuntimeState(SessionRuntimeState.Failed);
                    if (endReason == SessionEndReason.PeerEnded)
                    {
                        ApplyEndReasonPresentation(SessionEndReason.PeerEnded);
                    }
                    else
                    {
                        EnsureFailurePresentation(
                            "Connection failed",
                            "The session ended due to a connection problem.",
                            "Retry");
                        StatusText = string.IsNullOrWhiteSpace(sessionRuntime.StatusText)
                            ? UserErrorMapper.HelperDisconnected()
                            : sessionRuntime.StatusText;
                        ConnectionState = "Failed";
                        runtimeTerminalFailure = true;
                    }
                    break;
                case SessionRuntimeState.Disconnected:
                    IsConnecting = false;
                    if (endInvoked)
                    {
                        break;
                    }

                    endReason = ClassifyEndReasonForRuntimeState(SessionRuntimeState.Disconnected);
                    if (endReason == SessionEndReason.PeerEnded)
                    {
                        ApplyEndReasonPresentation(SessionEndReason.PeerEnded);
                    }
                    else
                    {
                        EnsureFailurePresentation(
                            "Connection failed",
                            "The session ended due to a connection problem.",
                            "Retry");
                        StatusText = string.IsNullOrWhiteSpace(sessionRuntime.StatusText)
                            ? UserErrorMapper.HelperDisconnected()
                            : sessionRuntime.StatusText;
                        ConnectionState = "Failed";
                        runtimeTerminalFailure = true;
                    }
                    break;
            }
        }

        var phaseReason = $"SyncFromRuntime:{sessionRuntime.State}";
        var phase = endSessionRequested
            ? SessionUiPhase.Ended
            : SessionUxPhaseMapper.FromRuntimeState(sessionRuntime.State, isHelper: true);
        if (runtimeTerminalFailure)
        {
            phase = SessionUiPhase.Failed;
            phaseReason = $"RuntimeTerminal:{sessionRuntime.State}";
        }
        else if (!endSessionRequested && endReason == SessionEndReason.PeerEnded)
        {
            phase = SessionUiPhase.Ended;
            phaseReason += ":PeerEnded";
        }
        else if (!endSessionRequested &&
                 endReason == SessionEndReason.Failed &&
                 sessionRuntime.State is (SessionRuntimeState.Failed or SessionRuntimeState.Disconnected))
        {
            var shouldRecover = sessionRuntime.IsTransientStatusVisible || BannerStatus.Kind == UserStatusKind.Reconnecting;
            phase = shouldRecover ? SessionUiPhase.Recovering : SessionUiPhase.Failed;
            phaseReason += shouldRecover ? ":Recovering" : ":Failed";
            TryShowUiRecoveryTransient(
                $"runtime:{sessionRuntime.State}:{phase}",
                BuildRecoveryTransientText(shouldRecover),
                canCancel: shouldRecover || sessionRuntime.CanCancelTransientStatus);
        }
        else if (!endSessionRequested &&
                 sessionRuntime.State is (SessionRuntimeState.Failed or SessionRuntimeState.Disconnected))
        {
            var shouldRecover = sessionRuntime.IsTransientStatusVisible || BannerStatus.Kind == UserStatusKind.Reconnecting;
            phase = shouldRecover ? SessionUiPhase.Recovering : SessionUiPhase.Failed;
            TryShowUiRecoveryTransient(
                $"runtime:{sessionRuntime.State}:{phase}",
                BuildRecoveryTransientText(shouldRecover),
                canCancel: shouldRecover || sessionRuntime.CanCancelTransientStatus);
        }
        else if (!endSessionRequested &&
                 phase is (SessionUiPhase.Idle or SessionUiPhase.Waiting) &&
                 (string.Equals(ConnectionState, "Failed", StringComparison.Ordinal) ||
                  string.Equals(ConnectionState, "Rejected", StringComparison.Ordinal)))
        {
            phase = SessionUiPhase.Failed;
        }
        else if (phase is SessionUiPhase.Connected or SessionUiPhase.Waiting or SessionUiPhase.Idle)
        {
            if (!sessionRuntime.IsTransientStatusVisible)
            {
                uiRecoveryTransientDismissed = false;
            }
            ClearUiRecoveryTransient();
        }

        if (endSessionRequested ||
            sessionRuntime.State is SessionRuntimeState.Rejected or SessionRuntimeState.Failed or SessionRuntimeState.Disconnected)
        {
            ClearRemoteScreenShareFrame();
        }

        SessionUxContext? phaseContext = null;
        if (runtimeTerminalFailure)
        {
            phaseContext = new SessionUxContext(FailureTitle, FailureMessage, FailureActionText);
        }
        else if (phase == SessionUiPhase.Failed &&
            (!string.IsNullOrWhiteSpace(FailureTitle) ||
             !string.IsNullOrWhiteSpace(FailureMessage) ||
             !string.IsNullOrWhiteSpace(FailureActionText)))
        {
            phaseContext = new SessionUxContext(FailureTitle, FailureMessage, FailureActionText);
        }

        uiStateStore?.SetPhase(phase, phaseReason, phaseContext);
        fallbackUiPhase = phase;
        ApplySessionBannerPolicy();
        UpdateUiFromSnapshot();

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
        EndSessionCommand.NotifyCanExecuteChanged();
        SyncTransientStatusFromRuntime();
        NotifyStatusBannerDetailChanged();
    }

    private void SyncTransientStatusFromRuntime()
    {
        if (IsConnectedView)
        {
            ClearUiRecoveryTransient();
            ShowTransientBanner = false;
            TransientBannerText = string.Empty;
            CanCancelTransient = false;
            return;
        }

        if (sessionRuntime.IsTransientStatusVisible)
        {
            var suppressAfterUserCancel =
                uiRecoveryTransientDismissed &&
                !IsConnecting &&
                sessionRuntime.State != SessionRuntimeState.Connecting;
            if (suppressAfterUserCancel)
            {
                ShowTransientBanner = false;
                TransientBannerText = string.Empty;
                CanCancelTransient = false;
                return;
            }

            ShowTransientBanner = true;
            TransientBannerText = SanitizeTransientText(sessionRuntime.TransientStatusText);
            CanCancelTransient = sessionRuntime.CanCancelTransientStatus;
            return;
        }

        if (hasUiRecoveryTransient && !uiRecoveryTransientDismissed)
        {
            ShowTransientBanner = true;
            TransientBannerText = SanitizeTransientText(uiRecoveryTransientText);
            CanCancelTransient = uiRecoveryTransientCanCancel;
            return;
        }

        ShowTransientBanner = false;
        TransientBannerText = string.Empty;
        CanCancelTransient = false;
    }

    private void ApplyFailurePresentation(SessionRuntimeState runtimeState)
    {
        var presentation = FailurePresentationPolicy.Resolve(runtimeState, ConnectionState, BannerStatus);
        if (presentation is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(FailureTitle))
        {
            FailureTitle = presentation.Title;
        }

        if (string.IsNullOrWhiteSpace(FailureMessage))
        {
            FailureMessage = presentation.Message;
        }

        if (string.IsNullOrWhiteSpace(FailureActionText))
        {
            FailureActionText = presentation.ActionText;
        }

        uiStateStore?.SetPhase(
            SessionUiPhase.Failed,
            $"SyncFromRuntime:{runtimeState}",
            new SessionUxContext(FailureTitle, FailureMessage, FailureActionText));
    }

    private void ClearFailurePresentation()
    {
        FailureTitle = string.Empty;
        FailureMessage = string.Empty;
        FailureActionText = string.Empty;
    }

    private void EnsureFailurePresentation(string title, string message, string actionText)
    {
        if (string.IsNullOrWhiteSpace(FailureTitle))
        {
            FailureTitle = title;
        }

        if (string.IsNullOrWhiteSpace(FailureMessage))
        {
            FailureMessage = message;
        }

        if (string.IsNullOrWhiteSpace(FailureActionText))
        {
            FailureActionText = actionText;
        }
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
        ApplySessionBannerPolicy();
    }

    private void ApplySessionBannerPolicy()
    {
        var phase = uiStateStore?.Phase ?? SessionUxPhaseMapper.FromRuntimeState(sessionRuntime.State, isHelper: true);
        var context = uiStateStore?.Context;
        var overrideStatus = SessionBannerPolicy.BuildPhaseStatusOverride(
            phase,
            presenterBannerStatus,
            context,
            StatusText);
        var effectiveStatus = overrideStatus ?? presenterBannerStatus;
        BannerStatus = effectiveStatus;

        var forceVisible = SessionBannerPolicy.ShouldForceVisible(phase);
        var statusVisible = BannerStatus.Kind is not UserStatusKind.Idle and not UserStatusKind.Connected;
        ShowStatusBanner = forceVisible || statusVisible || SessionBannerPolicy.ShouldShowStatusBanner(phase);
        StatusBannerDetailsText = SessionBannerPolicy.BuildDetailsText(
            phase,
            StatusBannerFailureCategory,
            StatusBannerSessionCorrelationId,
            StatusBannerLastConnectDuration,
            StatusBannerLastHandshakeDuration,
            StatusBannerBridgeState,
            context);

        var forceRetryState = string.Equals(ConnectionState, "Failed", StringComparison.Ordinal) ||
            string.Equals(ConnectionState, "Disconnected", StringComparison.Ordinal) ||
            string.Equals(ConnectionState, "Rejected", StringComparison.Ordinal);
        var nextCanStartOrConnect = forceRetryState || phase is SessionUiPhase.Idle
            or SessionUiPhase.Waiting
            or SessionUiPhase.Recovering
            or SessionUiPhase.Failed
            or SessionUiPhase.Ended;

        CanStartOrConnect = nextCanStartOrConnect;
        ConnectCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
    }

    private void UpdateUiFromSnapshot()
    {
        bool nextChatEnabled;
        bool nextCanSendFiles;
        bool nextCanEndSession;
        bool nextCanOpenDiagnostics;
        var phase = GetEffectivePhase();
        EffectivePhase = phase;
        nextCanEndSession = CanEndForPhase(phase);

        if (!FeatureFlags.UsePhaseDrivenGating || uiStateStore is null)
        {
            nextCanOpenDiagnostics = openDiagnosticsAction is not null &&
                phase is SessionUiPhase.Connecting
                    or SessionUiPhase.Connected
                    or SessionUiPhase.Recovering
                    or SessionUiPhase.Failed
                    or SessionUiPhase.Ended;
            nextCanSendFiles = phase is SessionUiPhase.Idle
                or SessionUiPhase.Waiting
                or SessionUiPhase.Connected
                or SessionUiPhase.Failed;
            nextChatEnabled = phase == SessionUiPhase.Connected;
        }
        else
        {
            switch (phase)
            {
                case SessionUiPhase.Connected:
                    nextChatEnabled = true;
                    nextCanSendFiles = true;
                    nextCanOpenDiagnostics = openDiagnosticsAction is not null;
                    break;

                case SessionUiPhase.Connecting:
                    nextChatEnabled = false;
                    nextCanSendFiles = false;
                    nextCanOpenDiagnostics = openDiagnosticsAction is not null;
                    break;

                case SessionUiPhase.Failed:
                case SessionUiPhase.Ended:
                    nextChatEnabled = false;
                    nextCanSendFiles = false;
                    nextCanOpenDiagnostics = openDiagnosticsAction is not null;
                    break;

                default:
                    nextChatEnabled = false;
                    nextCanSendFiles = false;
                    nextCanOpenDiagnostics = openDiagnosticsAction is not null;
                    break;
            }
        }

        IsChatInputEnabled = nextChatEnabled;
        CanSendFiles = nextCanSendFiles;
        CanEndSession = nextCanEndSession;
        CanOpenDiagnostics = nextCanOpenDiagnostics;

        OnPropertyChanged(nameof(ShowOpenDiagnosticsLink));
        SendChatCommand.NotifyCanExecuteChanged();
        OpenDiagnosticsCommand.NotifyCanExecuteChanged();
        EndSessionCommand.NotifyCanExecuteChanged();
        AssertUiConsistency();
    }

    private static bool CanEndForPhase(SessionUiPhase phase) =>
        phase is SessionUiPhase.Connecting
            or SessionUiPhase.Connected
            or SessionUiPhase.Recovering;

    private SessionUiPhase GetEffectivePhase()
    {
        if (FeatureFlags.UsePhaseDrivenGating && uiStateStore is not null)
        {
            return uiStateStore.Phase;
        }

        return fallbackUiPhase;
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

    private void TryShowUiRecoveryTransient(string key, string text, bool canCancel)
    {
        if (uiRecoveryTransientDismissed)
        {
            return;
        }

        var now = nowProvider();
        var normalizedKey = key ?? string.Empty;
        if (now < nextUiRecoveryBannerAllowedAt &&
            string.Equals(uiRecoveryTransientKey, normalizedKey, StringComparison.Ordinal))
        {
            return;
        }

        hasUiRecoveryTransient = true;
        uiRecoveryTransientText = SanitizeTransientText(text);
        uiRecoveryTransientCanCancel = canCancel;
        uiRecoveryTransientKey = normalizedKey;
        nextUiRecoveryBannerAllowedAt = now + RecoveryTransientThrottle;
    }

    private void ClearUiRecoveryTransient()
    {
        hasUiRecoveryTransient = false;
        uiRecoveryTransientText = string.Empty;
        uiRecoveryTransientCanCancel = false;
        uiRecoveryTransientKey = string.Empty;
    }

    private string BuildRecoveryTransientText(bool isRecovering)
    {
        var baseText = isRecovering
            ? "Connection lost. Reconnecting…"
            : "Connection failed. You can retry.";
        if (!IsInFailureCooldown())
        {
            return baseText;
        }

        var remaining = connectFailureCooldown - (nowProvider() - lastFailedAttemptUtc);
        if (remaining <= TimeSpan.Zero)
        {
            return baseText;
        }

        var seconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        return $"{baseText} Retry available in {seconds}s.";
    }

    private static string SanitizeTransientText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var withoutAttempt = AttemptLabelRegex.Replace(text, string.Empty).Trim();
        return string.IsNullOrWhiteSpace(withoutAttempt) ? text : withoutAttempt;
    }

    private static UserFacingStatus NormalizeStatusForDisplay(UserFacingStatus status)
    {
        return status with
        {
            Message = SanitizeTransientText(status.Message),
            Attempt = null
        };
    }

    [Conditional("DEBUG")]
    private void AssertUiConsistency()
    {
        if (IsConnectedView && ShowMainControls)
        {
            throw new InvalidOperationException("Helper UI invariant failed: connected view cannot show main controls.");
        }

        if (ShowConnectedPanel && !IsConnectedView)
        {
            throw new InvalidOperationException("Helper UI invariant failed: connected panel requires connected view.");
        }

        if (ShowChatPanel && !IsConnectedView)
        {
            throw new InvalidOperationException("Helper UI invariant failed: chat panel requires connected view.");
        }

        if (ShowFailurePanel && IsConnectedView)
        {
            throw new InvalidOperationException("Helper UI invariant failed: failure panel cannot be visible while connected.");
        }

        if (uiStateStore is not null && uiStateStore.Phase == SessionUiPhase.Connected &&
            !string.Equals(ConnectionState, "Connected", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Helper UI invariant failed: Connected phase requires ConnectionState=Connected.");
        }

        if (uiStateStore is not null && uiStateStore.Phase == SessionUiPhase.Failed && IsChatInputEnabled)
        {
            throw new InvalidOperationException("Helper UI invariant failed: Failed phase requires disabled chat input.");
        }

        if (IsChatInputEnabled &&
            uiStateStore?.Phase != SessionUiPhase.Connected &&
            sessionRuntime.State != SessionRuntimeState.Connected)
        {
            throw new InvalidOperationException("Helper UI invariant failed: chat input requires connected phase or runtime state.");
        }

        if (endInvoked && ShowTransientBanner)
        {
            throw new InvalidOperationException("Helper UI invariant failed: end-invoked state must not show transient banner.");
        }

        if (uiStateStore is not null &&
            uiStateStore.Phase is SessionUiPhase.Failed or SessionUiPhase.Ended or SessionUiPhase.Idle or SessionUiPhase.Waiting &&
            CanEndSession)
        {
            throw new InvalidOperationException("Helper UI invariant failed: Idle/Waiting/Failed/Ended phases require disabled end-session.");
        }

        if (uiStateStore is not null &&
            uiStateStore.Phase is SessionUiPhase.Ended or SessionUiPhase.Failed &&
            IsChatInputEnabled)
        {
            throw new InvalidOperationException("Helper UI invariant failed: Ended/Failed phase requires disabled chat input.");
        }

        if (HeaderStatusText.StartsWith("Connected", StringComparison.Ordinal) && !IsChatInputEnabled)
        {
            throw new InvalidOperationException("UI invariant failed: Connected header requires chat enabled.");
        }

        if (string.IsNullOrWhiteSpace(HeaderStatusText))
        {
            throw new InvalidOperationException("Helper UI invariant failed: header status text must not be empty.");
        }
    }

    private void PrepareForNewSession()
    {
        if (!endSessionRequested && !endSessionCancelInvoked && endReason is null)
        {
            return;
        }

        wasConnected = false;
        endInvoked = false;
        endReason = null;
        endSessionRequested = false;
        endSessionCancelInvoked = false;
        ClearRemoteScreenShareFrame();
        if (uiStateStore?.Phase == SessionUiPhase.Ended)
        {
            uiStateStore.SetPhase(SessionUiPhase.Waiting, "StartNewSession:Helper");
            ApplySessionBannerPolicy();
        }
    }

    private SessionEndReason ClassifyEndReasonForRuntimeState(SessionRuntimeState state)
    {
        if (state is not (SessionRuntimeState.Failed or SessionRuntimeState.Disconnected))
        {
            return SessionEndReason.Failed;
        }

        if (!wasConnected)
        {
            return SessionEndReason.Failed;
        }

        var inferredPeerEnded =
            sessionRuntime.LastDisconnectWasRemoteEnd ||
            (state == SessionRuntimeState.Disconnected && sessionRuntime.LastTransportFailure is null);
        return inferredPeerEnded ? SessionEndReason.PeerEnded : SessionEndReason.Failed;
    }

    private void ApplyEndReasonPresentation(SessionEndReason reason)
    {
        ClearRemoteScreenShareFrame();
        ClearUiRecoveryTransient();
        ShowTransientBanner = false;
        TransientBannerText = string.Empty;
        CanCancelTransient = false;

        switch (reason)
        {
            case SessionEndReason.UserEnded:
                uiRecoveryTransientDismissed = true;
                ClearFailurePresentation();
                ShowChatNotice = false;
                StatusText = "You ended the session.";
                ConnectionState = "Idle";
                break;
            case SessionEndReason.PeerEnded:
                uiRecoveryTransientDismissed = true;
                ClearFailurePresentation();
                ShowChatNotice = false;
                CodeInput = string.Empty;
                StatusText = "The other person ended the session.";
                ConnectionState = "Idle";
                break;
            case SessionEndReason.Failed:
                uiRecoveryTransientDismissed = false;
                if (wasConnected)
                {
                    CodeInput = string.Empty;
                }
                if (string.IsNullOrWhiteSpace(FailureTitle))
                {
                    FailureTitle = "Session ended";
                }

                if (string.IsNullOrWhiteSpace(FailureMessage))
                {
                    FailureMessage = "The session ended due to a connection problem.";
                }

                if (string.IsNullOrWhiteSpace(FailureActionText))
                {
                    FailureActionText = "Retry";
                }

                StatusText = "The session ended due to a connection problem.";
                ConnectionState = "Failed";
                break;
        }
    }

    private void ClearRemoteScreenShareFrame()
    {
        ScreenShareViewer.Clear();
    }

    private static string BuildScreenShareViewerMessage(string statusText)
    {
        if (string.IsNullOrWhiteSpace(statusText) ||
            string.Equals(statusText, "Live", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return string.Equals(statusText, "Invalid frame received", StringComparison.Ordinal)
            ? "Screen sharing is active, but the latest frame could not be displayed."
            : statusText;
    }

    private string AppendScreenShareSuffix(string text)
    {
        if (!ScreenShareViewer.IsActive)
        {
            return text;
        }

        return EffectivePhase is SessionUiPhase.Failed or SessionUiPhase.Ended
            ? text
            : $"{text} • Viewing screen";
    }

    private bool IsTransientBannerDuplicateWithHeader()
    {
        if (!ShowTransientBanner || string.IsNullOrWhiteSpace(TransientBannerText) || string.IsNullOrWhiteSpace(HeaderStatusText))
        {
            return false;
        }

        return HeaderStatusText.StartsWith(TransientBannerText, StringComparison.Ordinal) ||
               TransientBannerText.StartsWith(HeaderStatusText, StringComparison.Ordinal);
    }
}
