using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using NLink.App.Configuration;
using NLink.App.Services;
using NLink.App.Services.ScreenCapture;
using NLink.App.Threading;
using NLink.Core;
using NLink.Core.Chat;
using NLink.Infra.Nkn;

namespace NLink.App.ViewModels;

public sealed class HelpeePageViewModel : ViewModelBase, IDisposable, IChatPanelBindings
{
    private static readonly TimeSpan DefaultIncomingRequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RecoveryTransientThrottle = TimeSpan.FromSeconds(2);
#if DEBUG
    private static readonly TimeSpan PreviewSnapshotInterval = TimeSpan.FromSeconds(10);
#endif
    private static readonly Regex AttemptLabelRegex = new(@"\s*\(?attempt\s+\d+(?:,\s*next retry in \d+s)?\)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly Action cancelAction;
    private readonly Action backAction;
    private readonly Action? openDiagnosticsAction;
    private readonly TransportRuntimeConfig transportConfig;
    private readonly SessionRuntime sessionRuntime;
    private readonly StatusPresenter statusPresenter;
    private readonly bool ownsStatusPresenter;
    private readonly IClipboardService? clipboardService;
    private readonly SessionUiStateStore? uiStateStore;
    private SessionCode sessionCode = SessionCode.CreateRandom();
    private bool hasIncomingRequest;
    private bool isRequestAllowed;
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
    private bool hasUiRecoveryTransient;
    private string uiRecoveryTransientText = string.Empty;
    private bool uiRecoveryTransientCanCancel;
    private DateTimeOffset nextUiRecoveryBannerAllowedAt = DateTimeOffset.MinValue;
    private string uiRecoveryTransientKey = string.Empty;
    private bool uiRecoveryTransientDismissed;
    private SessionReliabilityAttempt? reliabilityAttempt;
    private readonly InlineTransientText copyFeedback = new();
    private CancellationTokenSource? incomingRequestTimeoutCts;
    private readonly TimeSpan incomingRequestTimeout;
    private bool startupBlocked;
    private bool autoRegeneratingAfterDisconnect;
    private UserFacingStatus bannerStatus = UserFacingStatus.IdleStatus;
    private UserFacingStatus presenterBannerStatus = UserFacingStatus.IdleStatus;
    private bool showStatusBanner;
    private string? statusBannerDetailsText;
    private bool canStartOrConnect = true;
    private bool canEndSession = true;
    private bool canOpenDiagnostics;
    private bool isChatInputEnabled;
    private SessionUiPhase effectivePhase;
    private bool endInvoked;
    private bool wasConnected;
    private SessionEndReason? endReason;
    private bool endSessionRequested;
    private bool endSessionCancelInvoked;
    private bool pendingAutoRegenerateAfterDisconnect;
    private SessionUiPhase lastObservedUiPhase;
    private readonly HelpeeScreenShareCoordinator screenShareCoordinator;
    private readonly bool isScreenCaptureSupported;
    private bool isScreenSharingPreviewActive;
    private Bitmap? screenSharePreviewFrame;
    private ScreenShareStatus screenSharePreviewStatus = new(ScreenShareState.Off, null, DateTimeOffset.UtcNow);
    private int screenSharePreviewStopInFlight;
    private bool disposed;
#if DEBUG
    private Timer? previewSnapshotTimer;
    private int previewSnapshotTickInFlight;
#endif

    public HelpeePageViewModel(
        Action cancelAction,
        TransportRuntimeConfig transportConfig,
        SessionRuntime sessionRuntime,
        Action? openDiagnosticsAction = null,
        IClipboardService? clipboardService = null,
        ShareMessageConfig? shareMessageConfig = null,
        StatusPresenter? statusPresenter = null,
        TimeSpan? incomingRequestTimeout = null,
        SessionUiStateStore? uiStateStore = null,
        Action? backAction = null,
        IScreenCaptureSourceFactory? screenCaptureSourceFactory = null)
        : this(
            cancelAction,
            transportConfig,
            sessionRuntime,
            openDiagnosticsAction,
            clipboardService,
            shareMessageConfig,
            statusPresenter,
            incomingRequestTimeout,
            uiStateStore,
            backAction,
            screenCaptureSourceFactory,
            decodeFrame: null)
    {
    }

    internal HelpeePageViewModel(
        Action cancelAction,
        TransportRuntimeConfig transportConfig,
        SessionRuntime sessionRuntime,
        Action? openDiagnosticsAction,
        IClipboardService? clipboardService,
        ShareMessageConfig? shareMessageConfig,
        StatusPresenter? statusPresenter,
        TimeSpan? incomingRequestTimeout,
        SessionUiStateStore? uiStateStore,
        Action? backAction,
        IScreenCaptureSourceFactory? screenCaptureSourceFactory,
        Func<byte[], Bitmap>? decodeFrame)
    {
        this.cancelAction = cancelAction;
        this.backAction = backAction ?? cancelAction;
        this.openDiagnosticsAction = openDiagnosticsAction;
        this.transportConfig = transportConfig;
        this.sessionRuntime = sessionRuntime;
        this.statusPresenter = statusPresenter ?? new StatusPresenter(sessionRuntime);
        ownsStatusPresenter = statusPresenter is null;
        this.clipboardService = clipboardService;
        this.incomingRequestTimeout = incomingRequestTimeout ?? DefaultIncomingRequestTimeout;
        this.uiStateStore = uiStateStore;
        var resolvedCaptureSourceFactory = screenCaptureSourceFactory ?? new DefaultScreenCaptureSourceFactory();
        isScreenCaptureSupported = DetermineIsCaptureSupported(resolvedCaptureSourceFactory);
        lastObservedUiPhase = uiStateStore?.Phase ?? SessionUiPhase.Idle;
        _ = shareMessageConfig;
        screenShareCoordinator = new HelpeeScreenShareCoordinator(
            isDisposed: () => disposed,
            canShowScreenShareAction: () => CanShowScreenShareAction,
            isPreviewActive: () => IsScreenSharingPreviewActive,
            captureSourceFactory: resolvedCaptureSourceFactory,
            setPreviewActive: value => IsScreenSharingPreviewActive = value,
            setStatus: value => ScreenSharePreviewStatus = value,
            getPreviewFrame: () => ScreenSharePreviewFrame,
            setPreviewFrame: value => ScreenSharePreviewFrame = value,
            decodeFrame: decodeFrame);

        ChatMessages = new ObservableCollection<ChatLineViewModel>();

        sessionRuntime.StateChanged += OnSessionRuntimeStateChanged;
        sessionRuntime.TransientStatusChanged += OnTransientStatusChanged;
        sessionRuntime.IncomingJoinRequestAvailable += OnIncomingJoinRequestAvailable;
        sessionRuntime.Disconnected += OnRuntimeDisconnected;
        sessionRuntime.ChatMessageReceived += OnChatMessageReceived;
        sessionRuntime.ChatMessageReceivedBeforeApproved += OnChatMessageReceivedBeforeApproved;
        sessionRuntime.ChatStateChanged += OnChatStateChanged;
        this.statusPresenter.StatusChanged += OnStatusPresenterChanged;
        if (this.uiStateStore is not null)
        {
            this.uiStateStore.PropertyChanged += OnUiStateStorePropertyChanged;
        }

        RegenerateCodeCommand = new RelayCommand(RegenerateCode);
        CopyCodeCommand = new AsyncRelayCommand(CopyCodeAsync);
        AllowCommand = new RelayCommand(AllowIncomingRequest, CanAllowIncomingRequest);
        DeclineCommand = new AsyncRelayCommand(DeclineIncomingRequestAsync, CanDeclineIncomingRequest);
        SendChatCommand = new AsyncRelayCommand(SendChatAsync, CanSendChat);
        RetryCommand = new AsyncRelayCommand(RetryAsync);
        CancelTransientCommand = new AsyncRelayCommand(CancelTransientAsync, CanCancelTransientOperation);
        OpenDiagnosticsCommand = new RelayCommand(OpenDiagnostics, CanOpenDiagnosticsCommand);
        CancelCommand = new RelayCommand(CancelAndGoBack);
        EndSessionCommand = new RelayCommand(EndSession, CanTriggerEndSession);
        ToggleScreenSharePreviewCommand = new RelayCommand(ToggleScreenSharePreview, CanToggleScreenSharePreview);

        InitializeStartupAvailabilityState();
        presenterBannerStatus = NormalizeStatusForDisplay(this.statusPresenter.CurrentStatus);
        BannerStatus = presenterBannerStatus;
        if (!IsStartupBlocked)
        {
            StartHosting();
        }
        SyncTransientStatusFromRuntime();
        if (this.uiStateStore is not null && this.uiStateStore.Phase == SessionUiPhase.Idle)
        {
            this.uiStateStore.SetPhase(
                IsStartupBlocked ? SessionUiPhase.Idle : SessionUiPhase.Waiting,
                "Constructor:HelpeeSeed");
        }
        ApplySessionBannerPolicy();
        UpdateUiFromSnapshot();
    }

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

    public string ConnectionStatus
    {
        get => connectionStatus;
        private set
        {
            if (SetProperty(ref connectionStatus, value))
            {
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
                OnPropertyChanged(nameof(StatusLineText));
                OnPropertyChanged(nameof(SecondaryActionText));
                OnPropertyChanged(nameof(ShowChatSection));
                OnPropertyChanged(nameof(ShowFailurePanel));
                OnPropertyChanged(nameof(HeaderStatusText));
                OnPropertyChanged(nameof(ChatConnectionPillText));
                OnPropertyChanged(nameof(ShowChatConnectionPill));
                OnPropertyChanged(nameof(ShowTransientStatusPanel));
                ApplySessionBannerPolicy();
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

    public string StatusLineText => IsIncomingRequestView
        ? "Waiting for you to allow."
        : IsConnectedView
            ? ConnectionStatus
            : ConnectionStatus;

    public string SecondaryActionText => IsConnectedView ? "Disconnect" : "New code";

    public string HeaderStatusText => BuildHeaderStatusText();

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
                OnPropertyChanged(nameof(ShowBackButton));
                OnPropertyChanged(nameof(ShowRetryAction));
                OnPropertyChanged(nameof(ShowOpenDiagnosticsLink));
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

    public string ChatConnectionPillText =>
        EffectivePhase switch
        {
            SessionUiPhase.Connected => "Connected",
            SessionUiPhase.Connecting => "Connecting…",
            SessionUiPhase.Recovering => "Reconnecting…",
            _ => "Not connected",
        };

    public bool ShowChatConnectionPill => !HeaderStatusText.StartsWith(ChatConnectionPillText, StringComparison.Ordinal);

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

    public bool IsChatReady => sessionRuntime.CanSendChat;

    public bool CanStartOrConnect
    {
        get => canStartOrConnect;
        private set
        {
            if (SetProperty(ref canStartOrConnect, value))
            {
                OnPropertyChanged(nameof(CanStartConnect));
            }
        }
    }

    public bool CanStartConnect => CanStartOrConnect;

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

    public bool CanShowScreenShareAction =>
        ComputeCanShowScreenShareAction(
            FeatureFlags.EnableScreenShareScaffold,
            FeatureFlags.EnableScreenShareCapture,
            FeatureFlags.EnableScreenSharePreview,
            isScreenCaptureSupported);

    public bool IsScreenSharingPreviewActive
    {
        get => isScreenSharingPreviewActive;
        private set
        {
            if (SetProperty(ref isScreenSharingPreviewActive, value))
            {
                OnPropertyChanged(nameof(HeaderStatusText));
                OnPropertyChanged(nameof(ShowChatConnectionPill));
                OnPropertyChanged(nameof(ShowTransientStatusPanel));
                ToggleScreenSharePreviewCommand.NotifyCanExecuteChanged();
#if DEBUG
                UpdatePreviewSnapshotTimer();
#endif
                SyncTransportScreenShareWithPreview(value);
            }
        }
    }

    public Bitmap? ScreenSharePreviewFrame
    {
        get => screenSharePreviewFrame;
        private set
        {
            var previousShowPreviewFrame = ShowScreenSharePreviewFrame;
            var previousShowMainContent = ShowHelpeeMainContent;
            var previousShowDefaultPlaceholder = ShowDefaultScreenSharePlaceholder;
            if (SetProperty(ref screenSharePreviewFrame, value))
            {
                if (previousShowPreviewFrame != ShowScreenSharePreviewFrame)
                {
                    OnPropertyChanged(nameof(ShowScreenSharePreviewFrame));
                }

                if (previousShowMainContent != ShowHelpeeMainContent)
                {
                    OnPropertyChanged(nameof(ShowHelpeeMainContent));
                }

                if (previousShowDefaultPlaceholder != ShowDefaultScreenSharePlaceholder)
                {
                    OnPropertyChanged(nameof(ShowDefaultScreenSharePlaceholder));
                }
            }
        }
    }

    public ScreenShareStatus ScreenSharePreviewStatus
    {
        get => screenSharePreviewStatus;
        private set
        {
            var previousShowPreviewFrame = ShowScreenSharePreviewFrame;
            var previousShowDefaultPlaceholder = ShowDefaultScreenSharePlaceholder;
            var previousShowViewerError = ShowScreenShareViewerError;
            var previousViewerMessage = ScreenShareViewerMessage;
            var previousHeaderStatusText = HeaderStatusText;
            if (SetProperty(ref screenSharePreviewStatus, value))
            {
                if (previousShowPreviewFrame != ShowScreenSharePreviewFrame)
                {
                    OnPropertyChanged(nameof(ShowScreenSharePreviewFrame));
                }

                if (previousShowDefaultPlaceholder != ShowDefaultScreenSharePlaceholder)
                {
                    OnPropertyChanged(nameof(ShowDefaultScreenSharePlaceholder));
                }

                if (previousShowViewerError != ShowScreenShareViewerError)
                {
                    OnPropertyChanged(nameof(ShowScreenShareViewerError));
                }

                if (!string.Equals(previousViewerMessage, ScreenShareViewerMessage, StringComparison.Ordinal))
                {
                    OnPropertyChanged(nameof(ScreenShareViewerMessage));
                }

                if (!string.Equals(previousHeaderStatusText, HeaderStatusText, StringComparison.Ordinal))
                {
                    OnPropertyChanged(nameof(HeaderStatusText));
                    OnPropertyChanged(nameof(ShowChatConnectionPill));
                    OnPropertyChanged(nameof(ShowTransientStatusPanel));
                }

                ToggleScreenSharePreviewCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool ShowScreenSharePreviewFrame =>
        FeatureFlags.EnableScreenSharePreview &&
        ScreenSharePreviewFrame is not null &&
        ScreenSharePreviewStatus.State != ScreenShareState.Failed;

    public bool ShowDefaultScreenSharePlaceholder => !ShowScreenSharePreviewFrame && !ShowScreenShareViewerError;

    public bool ShowScreenShareViewerError =>
        ScreenSharePreviewStatus.State == ScreenShareState.Failed &&
        !string.IsNullOrWhiteSpace(ScreenShareViewerMessage);

    public string ScreenShareViewerMessage => ScreenSharePreviewStatus.UserMessage ?? string.Empty;

    public bool ShowHelpeeMainContent => !ShowScreenSharePreviewFrame;

    public bool IsChatInputEnabled
    {
        get => isChatInputEnabled;
        private set => SetProperty(ref isChatInputEnabled, value);
    }

    public bool ShowChatNotice
    {
        get => showChatNotice;
        private set => SetProperty(ref showChatNotice, value);
    }

    public string ChatNoticeText => "You received a message";

    public IRelayCommand RegenerateCodeCommand { get; }

    public IAsyncRelayCommand CopyCodeCommand { get; }

    public RelayCommand AllowCommand { get; }

    public IAsyncRelayCommand DeclineCommand { get; }

    public IAsyncRelayCommand SendChatCommand { get; }

    public IAsyncRelayCommand RetryCommand { get; }
    public IAsyncRelayCommand CancelTransientCommand { get; }

    public IRelayCommand OpenDiagnosticsCommand { get; }

    public IRelayCommand CancelCommand { get; }
    public IRelayCommand EndSessionCommand { get; }
    public IRelayCommand ToggleScreenSharePreviewCommand { get; }
    public IRelayCommand StatusBannerCopyDiagnosticsCommand => OpenDiagnosticsCommand;
    public IAsyncRelayCommand StatusBannerCancelCommand => CancelTransientCommand;

    public bool ShowRetryAction => !IsStartupBlocked && ConnectionState == "Failed";
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

    public bool ShowOpenDiagnosticsLink => CanOpenDiagnostics;

    internal static bool ComputeCanShowScreenShareAction(
        bool enableScreenShareScaffold,
        bool enableScreenShareCapture,
        bool enableScreenSharePreview,
        bool isCaptureSupported)
    {
        return enableScreenShareScaffold &&
               enableScreenShareCapture &&
               enableScreenSharePreview &&
               isCaptureSupported;
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
        sessionRuntime.IncomingJoinRequestAvailable -= OnIncomingJoinRequestAvailable;
        sessionRuntime.Disconnected -= OnRuntimeDisconnected;
        sessionRuntime.ChatMessageReceived -= OnChatMessageReceived;
        sessionRuntime.ChatMessageReceivedBeforeApproved -= OnChatMessageReceivedBeforeApproved;
        sessionRuntime.ChatStateChanged -= OnChatStateChanged;
        statusPresenter.StatusChanged -= OnStatusPresenterChanged;
        if (uiStateStore is not null)
        {
            uiStateStore.PropertyChanged -= OnUiStateStorePropertyChanged;
        }
        if (ownsStatusPresenter)
        {
            statusPresenter.Dispose();
        }
        sessionRuntime.SetReliabilityAttempt(null);
#if DEBUG
        StopPreviewSnapshotTimer();
#endif
        RunBoundedSynchronousCleanup(screenShareCoordinator.StopAsync, TimeSpan.FromSeconds(2));
        copyFeedback.Dispose();
        incomingRequestTimeoutCts?.Cancel();
        incomingRequestTimeoutCts?.Dispose();
        RunSynchronousCleanup(sessionRuntime.ResetAsync);
    }

    private void RegenerateCode()
    {
        sessionCode = SessionCode.CreateRandom();
        OnPropertyChanged(nameof(ShareCode));
        autoRegeneratingAfterDisconnect = false;
        pendingAutoRegenerateAfterDisconnect = false;
        wasConnected = false;
        endInvoked = false;
        endReason = null;
        endSessionRequested = false;
        endSessionCancelInvoked = false;

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

    private void ToggleScreenSharePreview()
    {
        screenShareCoordinator.Toggle();
    }

    private bool CanToggleScreenSharePreview()
    {
        if (disposed || ScreenSharePreviewStatus.State == ScreenShareState.Starting)
        {
            return false;
        }

        return IsScreenSharingPreviewActive || CanShowScreenShareAction;
    }

    private async Task RetryAsync()
    {
        PrepareForNewSession();

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

    private bool CanOpenDiagnosticsCommand()
    {
        return CanOpenDiagnostics;
    }

    private bool CanTriggerEndSession()
    {
        return CanEndSession && !endInvoked;
    }

#if DEBUG
    private void UpdatePreviewSnapshotTimer()
    {
        if (IsScreenSharingPreviewActive)
        {
            if (previewSnapshotTimer is not null)
            {
                return;
            }

            previewSnapshotTimer = new Timer(
                static state => ((HelpeePageViewModel)state!).OnPreviewSnapshotTimerTick(),
                this,
                PreviewSnapshotInterval,
                PreviewSnapshotInterval);
            return;
        }

        StopPreviewSnapshotTimer();
    }

    private void StopPreviewSnapshotTimer()
    {
        Interlocked.Exchange(ref previewSnapshotTickInFlight, 0);
        var timer = Interlocked.Exchange(ref previewSnapshotTimer, null);
        timer?.Dispose();
    }

    private void OnPreviewSnapshotTimerTick()
    {
        if (Interlocked.Exchange(ref previewSnapshotTickInFlight, 1) == 1)
        {
            return;
        }

        try
        {
            if (!IsScreenSharingPreviewActive)
            {
                return;
            }

            var heapBytes = GC.GetTotalMemory(false);
            using var process = Process.GetCurrentProcess();
            Debug.WriteLine(
                $"[ScreenSharePreviewVm] Snapshot heap={heapBytes} ws={process.WorkingSet64} decoded={screenShareCoordinator.FramesDecoded} state={ScreenSharePreviewStatus.State}.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ScreenSharePreviewVm] Snapshot failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref previewSnapshotTickInFlight, 0);
        }
    }
#endif

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
        uiRecoveryTransientDismissed = true;
        ClearUiRecoveryTransient();
        ShowTransientBanner = false;
        TransientBannerText = string.Empty;
        CanCancelTransient = false;
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
        return IsChatInputEnabled && !string.IsNullOrWhiteSpace(ChatDraft) && sessionRuntime.CanSendChat;
    }

    private void AllowIncomingRequest()
    {
        if (!CanAllowIncomingRequest())
        {
            return;
        }
        LogReliability(SessionReliabilityStage.Approved);
        LogReliability(SessionReliabilityStage.Completed);

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

        try
        {
            var sent = await sessionRuntime.TrySendChatTextAsync(draft, CancellationToken.None);
            if (sent is not null)
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
        RequestStopScreenSharePreview();
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
        if (wasConnected || IsConnectedView || sessionRuntime.State == SessionRuntimeState.Connected)
        {
            pendingAutoRegenerateAfterDisconnect = true;
        }
        uiRecoveryTransientDismissed = true;
        ClearUiRecoveryTransient();
        ShowTransientBanner = false;
        TransientBannerText = string.Empty;
        CanCancelTransient = false;
        RequestStopScreenSharePreview();
        uiStateStore?.SetPhase(SessionUiPhase.Ended, "UserEndSession");
        EffectivePhase = SessionUiPhase.Ended;
        IsChatInputEnabled = false;
        CanEndSession = false;
        SendChatCommand.NotifyCanExecuteChanged();
        EndSessionCommand.NotifyCanExecuteChanged();
        AssertUiConsistency();

        if (endSessionCancelInvoked)
        {
            return;
        }

        endSessionCancelInvoked = true;
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
        if (!RotateCodeAfterTerminalSession())
        {
            ConnectionStatus = "Waiting for helper…";
            ConnectionState = "Waiting";
        }
    }

    private void StartHosting()
    {
        PrepareForNewSession();

        if (IsStartupBlocked)
        {
            return;
        }

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
        wasConnected = true;
        endReason = null;
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

        if (ShouldQueueAutoRegenerateAfterTerminalTransition())
        {
            pendingAutoRegenerateAfterDisconnect = true;
        }

        _ = UiThreadDispatch.RunAsync(() =>
        {
            if (HasIncomingRequest && !IsRequestAllowed)
            {
                CancelIncomingRequestTimeout();
                HasIncomingRequest = false;
                IsRequestAllowed = false;
                if (!RotateCodeAfterTerminalSession())
                {
                    ConnectionStatus = "Waiting for helper…";
                    ConnectionState = "Waiting";
                }

                return;
            }

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
    }

    private void RemoveChatLine(ChatLineViewModel line)
    {
        ChatMessages.Remove(line);
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

    private void OnUiStateStorePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (disposed || uiStateStore is null || e.PropertyName != nameof(SessionUiStateStore.Phase))
        {
            return;
        }

        var previousPhase = lastObservedUiPhase;
        var nextPhase = uiStateStore.Phase;
        lastObservedUiPhase = nextPhase;

        _ = UiThreadDispatch.RunAsync(() =>
        {
            SessionUiDebug.LogPhaseChange(
                nameof(HelpeePageViewModel),
                previousPhase,
                nextPhase,
                sessionRuntime.State);
            UpdateUiFromSnapshot();
        });
    }

    private void SyncFromRuntime()
    {
        var autoRegeneratedAfterDisconnect = false;
        var runtimeTerminalFailure = false;
        if (endSessionRequested)
        {
            if (pendingAutoRegenerateAfterDisconnect &&
                sessionRuntime.State is SessionRuntimeState.Idle or SessionRuntimeState.Disconnected or SessionRuntimeState.Waiting or SessionRuntimeState.Failed)
            {
                endSessionRequested = false;
                endReason = null;
                autoRegeneratedAfterDisconnect = TryAutoRegenerateAfterConnectedSessionEnd();
                if (!autoRegeneratedAfterDisconnect)
                {
                    pendingAutoRegenerateAfterDisconnect = false;
                    endSessionRequested = true;
                    ApplyEndReasonPresentation(SessionEndReason.UserEnded);
                }
            }
            else
            {
                ApplyEndReasonPresentation(SessionEndReason.UserEnded);
            }
        }
        else
        {
            switch (sessionRuntime.State)
            {
                case SessionRuntimeState.IncomingJoinRequest:
                    endReason = null;
                    ClearFailurePresentation();
                    HasIncomingRequest = true;
                    IsRequestAllowed = false;
                    ConnectionStatus = sessionRuntime.StatusText;
                    ConnectionState = "IncomingRequest";
                    break;

                case SessionRuntimeState.Connected:
                    wasConnected = true;
                    endReason = null;
                    ClearFailurePresentation();
                    CancelIncomingRequestTimeout();
                    HasIncomingRequest = false;
                    IsRequestAllowed = true;
                    ConnectionStatus = transportConfig.AllowStatusText;
                    ConnectionState = "Connected";
                    break;

                case SessionRuntimeState.Rejected:
                    if (ShouldQueueAutoRegenerateAfterTerminalTransition())
                    {
                        pendingAutoRegenerateAfterDisconnect = true;
                    }
                    if (TryAutoRegenerateAfterConnectedSessionEnd())
                    {
                        endReason = null;
                        autoRegeneratedAfterDisconnect = true;
                        break;
                    }

                    if (endInvoked)
                    {
                        break;
                    }

                    endReason = SessionEndReason.Failed;
                    CancelIncomingRequestTimeout();
                    HasIncomingRequest = false;
                    IsRequestAllowed = false;
                    ConnectionStatus = "Request was rejected.";
                    ConnectionState = "Failed";
                    EnsureFailurePresentation(
                        "Request rejected",
                        "The other side declined the session.",
                        "Start new session");
                    runtimeTerminalFailure = true;
                    break;

                case SessionRuntimeState.Disconnected:
                    if (ShouldQueueAutoRegenerateAfterTerminalTransition())
                    {
                        pendingAutoRegenerateAfterDisconnect = true;
                    }
                    if (TryAutoRegenerateAfterConnectedSessionEnd())
                    {
                        endReason = null;
                        autoRegeneratedAfterDisconnect = true;
                        break;
                    }

                    if (endInvoked)
                    {
                        break;
                    }

                    endReason = SessionEndReason.Failed;
                    CancelIncomingRequestTimeout();
                    if (!HasIncomingRequest && !IsRequestAllowed)
                    {
                        EnsureFailurePresentation(
                            "Connection failed",
                            "The session ended due to a connection problem.",
                            "Retry");
                        ConnectionStatus = "Connection lost.";
                        ConnectionState = "Failed";
                        runtimeTerminalFailure = true;
                    }
                    break;

                case SessionRuntimeState.Waiting:
                    var waitingAfterIncomingRequestCancel = HasIncomingRequest && !IsRequestAllowed;
                    if (waitingAfterIncomingRequestCancel)
                    {
                        CancelIncomingRequestTimeout();
                        HasIncomingRequest = false;
                        IsRequestAllowed = false;
                        if (!IsStartupBlocked)
                        {
                            pendingAutoRegenerateAfterDisconnect = true;
                        }
                    }

                    if (!HasIncomingRequest &&
                        !IsRequestAllowed &&
                        ShouldQueueAutoRegenerateAfterTerminalTransition())
                    {
                        pendingAutoRegenerateAfterDisconnect = true;
                    }
                    if (TryAutoRegenerateAfterConnectedSessionEnd())
                    {
                        endReason = null;
                        autoRegeneratedAfterDisconnect = true;
                        break;
                    }

                    if (!HasIncomingRequest && !IsRequestAllowed)
                    {
                        if (wasConnected && endReason is null)
                        {
                            if (!endInvoked)
                            {
                                endReason = SessionEndReason.Failed;
                                EnsureFailurePresentation(
                                    "Connection failed",
                                    "The session ended due to a connection problem.",
                                    "Retry");
                                ConnectionStatus = "Connection lost.";
                                ConnectionState = "Failed";
                                runtimeTerminalFailure = true;
                            }
                        }
                        else if (endReason is null)
                        {
                            ConnectionStatus = string.IsNullOrWhiteSpace(sessionRuntime.StatusText)
                                ? "Waiting for helper…"
                                : sessionRuntime.StatusText;
                            ConnectionState = "Waiting";
                            ClearFailurePresentation();
                        }
                    }
                    break;

                case SessionRuntimeState.Failed:
                    if (ShouldQueueAutoRegenerateAfterTerminalTransition())
                    {
                        pendingAutoRegenerateAfterDisconnect = true;
                    }
                    if (TryAutoRegenerateAfterConnectedSessionEnd())
                    {
                        endReason = null;
                        autoRegeneratedAfterDisconnect = true;
                        break;
                    }

                    if (endInvoked)
                    {
                        break;
                    }

                    endReason = SessionEndReason.Failed;
                    CancelIncomingRequestTimeout();
                    HasIncomingRequest = false;
                    IsRequestAllowed = false;
                    EnsureFailurePresentation(
                        "Connection failed",
                        "The session ended due to a connection problem.",
                        "Retry");
                    ConnectionStatus = string.IsNullOrWhiteSpace(sessionRuntime.StatusText)
                        ? transportConfig.HelpeeDisconnectedText
                        : sessionRuntime.StatusText;
                    ConnectionState = "Failed";
                    runtimeTerminalFailure = true;
                    break;
            }
        }

        var phaseReason = $"SyncFromRuntime:{sessionRuntime.State}";
        var phase = autoRegeneratedAfterDisconnect
            ? SessionUiPhase.Waiting
            : endSessionRequested
                ? SessionUiPhase.Ended
                : SessionUxPhaseMapper.FromRuntimeState(sessionRuntime.State, isHelper: false);
        if (autoRegeneratedAfterDisconnect)
        {
            phaseReason += ":AutoRegenerated";
        }
        else if (runtimeTerminalFailure)
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
        else if (phase is SessionUiPhase.Connected or SessionUiPhase.Waiting or SessionUiPhase.Idle)
        {
            uiRecoveryTransientDismissed = false;
            ClearUiRecoveryTransient();
        }

        if (endSessionRequested ||
            sessionRuntime.State is SessionRuntimeState.Rejected or SessionRuntimeState.Failed or SessionRuntimeState.Disconnected)
        {
            RequestStopScreenSharePreview();
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
        ApplySessionBannerPolicy();
        UpdateUiFromSnapshot();

        OnPropertyChanged(nameof(ShowRetryAction));
        OnPropertyChanged(nameof(ShowOpenDiagnosticsLink));
        OnPropertyChanged(nameof(ShowFailurePanel));
        RetryCommand.NotifyCanExecuteChanged();
        OpenDiagnosticsCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsChatReady));
        SendChatCommand.NotifyCanExecuteChanged();
        AllowCommand.NotifyCanExecuteChanged();
        DeclineCommand.NotifyCanExecuteChanged();
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
            if (ShouldSuppressPassiveHostingTransient(sessionRuntime.TransientStatusText))
            {
                ShowTransientBanner = false;
                TransientBannerText = string.Empty;
                CanCancelTransient = false;
                return;
            }

            ShowTransientBanner = true;
            TransientBannerText = SanitizeTransientText(sessionRuntime.TransientStatusText);
            CanCancelTransient = sessionRuntime.CanCancelTransientStatus && !IsIncomingRequestView;
            return;
        }

        if (hasUiRecoveryTransient && !uiRecoveryTransientDismissed)
        {
            if (ShouldSuppressPassiveHostingTransient(uiRecoveryTransientText))
            {
                ShowTransientBanner = false;
                TransientBannerText = string.Empty;
                CanCancelTransient = false;
                return;
            }

            ShowTransientBanner = true;
            TransientBannerText = SanitizeTransientText(uiRecoveryTransientText);
            CanCancelTransient = uiRecoveryTransientCanCancel && !IsIncomingRequestView;
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
        ApplySessionBannerPolicy();
    }

    private void ApplySessionBannerPolicy()
    {
        var phase = uiStateStore?.Phase ?? SessionUxPhaseMapper.FromRuntimeState(sessionRuntime.State, isHelper: false);
        var context = uiStateStore?.Context;
        var overrideStatus = SessionBannerPolicy.BuildPhaseStatusOverride(
            phase,
            presenterBannerStatus,
            context,
            ConnectionStatus);
        var effectiveStatus = overrideStatus ?? presenterBannerStatus;
        BannerStatus = effectiveStatus;

        var suppressed = IsIncomingRequestView;
        var forceVisible = SessionBannerPolicy.ShouldForceVisible(phase);
        var statusVisible = BannerStatus.Kind is not UserStatusKind.Idle and not UserStatusKind.Connected;
        ShowStatusBanner = !suppressed && (forceVisible || statusVisible || SessionBannerPolicy.ShouldShowStatusBanner(phase));
        StatusBannerDetailsText = SessionBannerPolicy.BuildDetailsText(
            phase,
            StatusBannerFailureCategory,
            StatusBannerSessionCorrelationId,
            StatusBannerLastConnectDuration,
            StatusBannerLastHandshakeDuration,
            StatusBannerBridgeState,
            context);
    }

    private void UpdateUiFromSnapshot()
    {
        bool nextChatEnabled;
        bool nextCanStartOrConnect;
        bool nextCanEndSession;
        bool nextCanOpenDiagnostics;
        var phase = GetEffectivePhase();
        EffectivePhase = phase;
        nextCanEndSession = CanEndForPhase(phase);

        if (!FeatureFlags.UsePhaseDrivenGating || uiStateStore is null)
        {
            nextCanStartOrConnect = phase is SessionUiPhase.Idle
                or SessionUiPhase.Waiting
                or SessionUiPhase.Recovering
                or SessionUiPhase.Failed
                or SessionUiPhase.Ended;
            nextCanOpenDiagnostics = openDiagnosticsAction is not null &&
                phase is SessionUiPhase.Connecting
                    or SessionUiPhase.Connected
                    or SessionUiPhase.Recovering
                    or SessionUiPhase.Failed
                    or SessionUiPhase.Ended;
            nextChatEnabled = phase == SessionUiPhase.Connected;
        }
        else
        {
            switch (phase)
            {
                case SessionUiPhase.Connected:
                    nextChatEnabled = true;
                    nextCanStartOrConnect = false;
                    nextCanOpenDiagnostics = openDiagnosticsAction is not null;
                    break;

                case SessionUiPhase.Connecting:
                    nextChatEnabled = false;
                    nextCanStartOrConnect = false;
                    nextCanOpenDiagnostics = openDiagnosticsAction is not null;
                    break;

                case SessionUiPhase.Failed:
                case SessionUiPhase.Ended:
                    nextChatEnabled = false;
                    nextCanStartOrConnect = !IsStartupBlocked;
                    nextCanOpenDiagnostics = openDiagnosticsAction is not null;
                    break;

                case SessionUiPhase.Idle:
                case SessionUiPhase.Waiting:
                    nextChatEnabled = false;
                    nextCanStartOrConnect = !IsStartupBlocked;
                    nextCanOpenDiagnostics = false;
                    break;

                default:
                    nextChatEnabled = false;
                    nextCanStartOrConnect = !IsStartupBlocked;
                    nextCanOpenDiagnostics = openDiagnosticsAction is not null;
                    break;
            }
        }

        IsChatInputEnabled = nextChatEnabled;
        CanStartOrConnect = nextCanStartOrConnect;
        CanEndSession = nextCanEndSession;
        CanOpenDiagnostics = nextCanOpenDiagnostics;

        OnPropertyChanged(nameof(ShowOpenDiagnosticsLink));
        OpenDiagnosticsCommand.NotifyCanExecuteChanged();
        SendChatCommand.NotifyCanExecuteChanged();
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

        return SessionUxPhaseMapper.FromRuntimeState(sessionRuntime.State, isHelper: false);
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

        var now = DateTimeOffset.UtcNow;
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

    private static string BuildRecoveryTransientText(bool isRecovering)
        => isRecovering
            ? "Connection lost. Reconnecting…"
            : "Connection failed. You can retry.";

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
        if (IsIncomingRequestView && ShowWaitingPanel)
        {
            throw new InvalidOperationException("Helpee UI invariant failed: incoming request view cannot show waiting panel.");
        }

        if (ShowIncomingRequestPanel && !IsIncomingRequestView)
        {
            throw new InvalidOperationException("Helpee UI invariant failed: incoming request panel requires incoming request view.");
        }

        if (ShowConnectedPanel && !IsConnectedView)
        {
            throw new InvalidOperationException("Helpee UI invariant failed: connected panel requires connected view.");
        }

        if (ShowFailurePanel && IsIncomingRequestView)
        {
            throw new InvalidOperationException("Helpee UI invariant failed: failure panel cannot be visible during incoming request view.");
        }

        if (uiStateStore is not null && uiStateStore.Phase == SessionUiPhase.Connected &&
            !string.Equals(ConnectionState, "Connected", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Helpee UI invariant failed: Connected phase requires ConnectionState=Connected.");
        }

        if (uiStateStore is not null && uiStateStore.Phase == SessionUiPhase.Failed && IsChatInputEnabled)
        {
            throw new InvalidOperationException("Helpee UI invariant failed: Failed phase requires disabled chat input.");
        }

        if (IsChatInputEnabled &&
            uiStateStore?.Phase != SessionUiPhase.Connected &&
            sessionRuntime.State != SessionRuntimeState.Connected)
        {
            throw new InvalidOperationException("Helpee UI invariant failed: chat input requires connected phase or runtime state.");
        }

        if (endInvoked && ShowTransientBanner)
        {
            throw new InvalidOperationException("Helpee UI invariant failed: end-invoked state must not show transient banner.");
        }

        if (uiStateStore is not null &&
            uiStateStore.Phase is SessionUiPhase.Failed or SessionUiPhase.Ended or SessionUiPhase.Idle or SessionUiPhase.Waiting &&
            CanEndSession)
        {
            throw new InvalidOperationException("Helpee UI invariant failed: Idle/Waiting/Failed/Ended phases require disabled end-session.");
        }

        if (uiStateStore is not null &&
            uiStateStore.Phase is SessionUiPhase.Ended or SessionUiPhase.Failed &&
            IsChatInputEnabled)
        {
            throw new InvalidOperationException("Helpee UI invariant failed: Ended/Failed phase requires disabled chat input.");
        }

        if (HeaderStatusText.StartsWith("Connected", StringComparison.Ordinal) && !IsChatInputEnabled)
        {
            throw new InvalidOperationException("UI invariant failed: Connected header requires chat enabled.");
        }

        if (string.IsNullOrWhiteSpace(HeaderStatusText))
        {
            throw new InvalidOperationException("Helpee UI invariant failed: header status text must not be empty.");
        }
    }

    private bool ShouldSuppressPassiveHostingTransient(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (IsIncomingRequestView)
        {
            return text.StartsWith("Connecting", StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith("Reconnecting", StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith("Connection lost", StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith("Connection failed", StringComparison.OrdinalIgnoreCase);
        }

        if (!IsWaitingView || HasIncomingRequest || IsRequestAllowed)
        {
            return false;
        }

        return text.StartsWith("Connecting", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("Reconnecting", StringComparison.OrdinalIgnoreCase);
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
                pendingAutoRegenerateAfterDisconnect = true;
                if (!RotateCodeAfterTerminalSession())
                {
                    ConnectionStatus = "No response yet.";
                    ConnectionState = "Waiting";
                }
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
        if (!pendingAutoRegenerateAfterDisconnect || autoRegeneratingAfterDisconnect || IsStartupBlocked)
        {
            return false;
        }

        pendingAutoRegenerateAfterDisconnect = false;
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

    private bool RotateCodeAfterTerminalSession()
    {
        if (IsStartupBlocked || disposed)
        {
            return false;
        }

        pendingAutoRegenerateAfterDisconnect = true;
        endSessionRequested = false;
        endReason = null;
        return TryAutoRegenerateAfterConnectedSessionEnd();
    }

    private bool ShouldQueueAutoRegenerateAfterTerminalTransition()
    {
        if (IsStartupBlocked || disposed || autoRegeneratingAfterDisconnect)
        {
            return false;
        }

        return wasConnected ||
               HasIncomingRequest ||
               IsRequestAllowed ||
               IsIncomingRequestView ||
               IsConnectedView ||
               endSessionRequested ||
               endReason is not null;
    }

    private void PrepareForNewSession()
    {
        RequestStopScreenSharePreview();

        if (!endSessionRequested && !endSessionCancelInvoked && endReason is null)
        {
            return;
        }

        wasConnected = false;
        endInvoked = false;
        endReason = null;
        endSessionRequested = false;
        endSessionCancelInvoked = false;
        if (uiStateStore?.Phase == SessionUiPhase.Ended)
        {
            uiStateStore.SetPhase(SessionUiPhase.Waiting, "StartNewSession:Helpee");
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
        ClearUiRecoveryTransient();
        ShowTransientBanner = false;
        TransientBannerText = string.Empty;
        CanCancelTransient = false;

        switch (reason)
        {
            case SessionEndReason.UserEnded:
                uiRecoveryTransientDismissed = true;
                CancelIncomingRequestTimeout();
                HasIncomingRequest = false;
                IsRequestAllowed = false;
                ShowChatNotice = false;
                ClearFailurePresentation();
                ConnectionStatus = "You ended the session.";
                ConnectionState = "Waiting";
                break;
            case SessionEndReason.PeerEnded:
                uiRecoveryTransientDismissed = true;
                CancelIncomingRequestTimeout();
                HasIncomingRequest = false;
                IsRequestAllowed = false;
                ShowChatNotice = false;
                ClearFailurePresentation();
                ConnectionStatus = "The other side ended the session.";
                ConnectionState = "Waiting";
                break;
            case SessionEndReason.Failed:
                uiRecoveryTransientDismissed = false;
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

                ConnectionStatus = "The session ended due to a connection problem.";
                ConnectionState = "Failed";
                break;
        }
    }

    private void RequestStopScreenSharePreview()
    {
        if (Interlocked.Exchange(ref screenSharePreviewStopInFlight, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await screenShareCoordinator.StopAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort teardown.
            }
            finally
            {
                Interlocked.Exchange(ref screenSharePreviewStopInFlight, 0);
            }
        });
    }

    private void SyncTransportScreenShareWithPreview(bool isPreviewActive)
    {
        if (disposed ||
            !FeatureFlags.EnableScreenShareTransport ||
            !FeatureFlags.EnableScreenShareCapture)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (isPreviewActive)
                {
                    await sessionRuntime.StartTransportScreenShareAsync().ConfigureAwait(false);
                }
                else
                {
                    await sessionRuntime.StopTransportScreenShareAsync("preview_stopped").ConfigureAwait(false);
                }
            }
            catch
            {
                // Best-effort: the local preview remains the user-visible source of truth.
            }
        });
    }

    private static bool DetermineIsCaptureSupported(IScreenCaptureSourceFactory captureSourceFactory)
    {
        ArgumentNullException.ThrowIfNull(captureSourceFactory);

        var source = captureSourceFactory.Create();
        try
        {
            return source.IsSupported;
        }
        finally
        {
            if (source is IAsyncDisposable asyncDisposable)
            {
                asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }

    private string BuildHeaderStatusText()
    {
        var baseText = EffectivePhase switch
        {
            SessionUiPhase.Connecting => "Connecting…",
            SessionUiPhase.Recovering => "Reconnecting…",
            SessionUiPhase.Connected => "Connected",
            SessionUiPhase.Failed => string.IsNullOrWhiteSpace(FailureTitle) ? "Connection failed" : FailureTitle,
            SessionUiPhase.Ended => !string.IsNullOrWhiteSpace(ConnectionStatus)
                ? ConnectionStatus
                : !string.IsNullOrWhiteSpace(FailureTitle)
                    ? FailureTitle
                    : "Session ended",
            _ => !string.IsNullOrWhiteSpace(ConnectionStatus) ? ConnectionStatus : "Ready",
        };

        if (ScreenSharePreviewStatus.State == ScreenShareState.Failed &&
            !string.IsNullOrWhiteSpace(ScreenShareViewerMessage) &&
            EffectivePhase is not (SessionUiPhase.Failed or SessionUiPhase.Ended))
        {
            return $"{baseText} • {ScreenShareViewerMessage}";
        }

        return AppendScreenShareSuffix(baseText);
    }

    private static void RunSynchronousCleanup(Func<Task> cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);

        if (Application.Current is not null && Dispatcher.UIThread.CheckAccess())
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await cleanup().ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort sync dispose cleanup.
                }
            });
            return;
        }

        try
        {
            cleanup().GetAwaiter().GetResult();
        }
        catch
        {
            // Best-effort sync dispose cleanup.
        }
    }

    private static void RunBoundedSynchronousCleanup(Func<Task> cleanup, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(cleanup);

        try
        {
            cleanup().WaitAsync(timeout).GetAwaiter().GetResult();
        }
        catch
        {
            // Best-effort bounded cleanup.
        }
    }

    private string AppendScreenShareSuffix(string text)
    {
        if (!IsScreenSharingPreviewActive)
        {
            return text;
        }

        return EffectivePhase is SessionUiPhase.Failed or SessionUiPhase.Ended
            ? text
            : $"{text} • Screen sharing";
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
