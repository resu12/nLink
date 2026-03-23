using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using NLink.App.Configuration;
using NLink.App.Services;
using NLink.App.Services.RemoteControl;
using NLink.App.Services.SessionConnect;
using NLink.App.Threading;
using NLink.Core;
using NLink.Core.Chat;
using NLink.Core.Diagnostics;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using NLink.Core.RemoteControl;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Infra.Nkn;

namespace NLink.App.ViewModels;

public sealed class HelperPageViewModel : ViewModelBase, IDisposable, IChatPanelBindings, IWindowCloseAware
{
    private static readonly TimeSpan DefaultConnectFailureCooldown = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultApprovalTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DisposeOperationTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RecoveryTransientThrottle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan EndSessionAfterControlStopGuard = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RemoteControlSnapshotKeepAliveInterval = TimeSpan.FromMilliseconds(250);
    private static readonly Regex AttemptLabelRegex = new(@"\s*\(?attempt\s+\d+(?:,\s*next retry in \d+s)?\)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly bool RemoteControlDebugPanelEnabled =
#if DEBUG
        true;
#else
        false;
#endif

    private readonly Action cancelAction;
    private readonly Action backAction;
    private readonly Action? openDiagnosticsAction;
    private readonly TransportRuntimeConfig transportConfig;
    private readonly SessionRuntime sessionRuntime;
    private readonly StatusPresenter statusPresenter;
    private readonly bool ownsStatusPresenter;
    private readonly IClipboardService? clipboardService;
    private readonly IInviteShareService inviteShareService;
    private readonly ShareMessageConfig shareMessageConfig;
    private readonly SessionUiStateStore? uiStateStore;
    private readonly IConnectInputResolver connectInputResolver;
    private readonly DispatcherTimer remoteControlStateSnapshotTimer;
    private readonly Func<CancellationToken, Task<PeerAddress?>> bootstrapHelperIdentityResolver;
    private string automaticIdentityRecoveryWarning = string.Empty;
    private CancellationTokenSource? bootstrapHelperIdentityResolutionCts;
    private PeerAddress? bootstrapHelperIdentity;
    private PeerAddress? previewInviteBoundHelperIdentity;
    private bool helperIdentityBootstrapPending;
    private string helperIdentityBootstrapErrorText = string.Empty;
    private string lastChatPanelStateLog = string.Empty;
    private long chatSendAttemptCounter;

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
    private FileTransferPanelItemViewModel? inboundFileTransfer;
    private FileTransferPanelItemViewModel? outboundFileTransfer;
    private bool isChatInputEnabled;
    private bool controlModeEnabled;
    private SessionUiPhase effectivePhase;
    private bool endInvoked;
    private bool wasConnected;
    private SessionEndReason? endReason;
    private bool endSessionRequested;
    private bool endSessionCancelInvoked;
    private DateTimeOffset endSessionGuardUntilUtc = DateTimeOffset.MinValue;
    private CancellationTokenSource? connectCts;
    private Task? bootstrapHelperIdentityResolutionTask;
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
    private int remoteControlDebugMouseMovesPerSecond;
    private int remoteControlDebugMouseMovesInWindow;
    private long remoteControlDebugMouseMoveWindowStartTickMs;
    private RemoteControlModifiersMask remoteControlHeldModifiersMask = RemoteControlModifiersMask.None;
    private RemoteControlMouseButtonsMask remoteControlHeldMouseButtonsMask = RemoteControlMouseButtonsMask.None;
    private RemoteControlModifiersMask remoteControlLastSentModifiersMask = RemoteControlModifiersMask.None;
    private RemoteControlMouseButtonsMask remoteControlLastSentMouseButtonsMask = RemoteControlMouseButtonsMask.None;
    private bool remoteControlSnapshotHasSent;
    private long remoteControlLastSnapshotSentTickMs;
    private bool remoteControlSnapshotImmediateRequested;
    private bool remoteControlSnapshotSendInProgress;
    private long remoteControlSnapshotSequence;
    private long remoteControlLastSnapshotSentSeq;
    private int remoteControlSnapshotSendsPerSecond;
    private int remoteControlSnapshotSendsInWindow;
    private long remoteControlSnapshotSendWindowStartTickMs;
    private bool remoteControlDebugPanelExpanded;
    private bool disposed;
    private int windowCloseDisconnectStarted;

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
        Action? backAction = null,
        IConnectInputResolver? connectInputResolver = null,
        Func<CancellationToken, Task<PeerAddress?>>? bootstrapHelperIdentityResolver = null,
        IInviteShareService? inviteShareService = null)
    {
        this.cancelAction = cancelAction;
        this.backAction = backAction ?? cancelAction;
        this.openDiagnosticsAction = openDiagnosticsAction;
        this.transportConfig = transportConfig;
        this.sessionRuntime = sessionRuntime;
        this.statusPresenter = statusPresenter ?? new StatusPresenter(sessionRuntime);
        ownsStatusPresenter = statusPresenter is null;
        this.clipboardService = clipboardService;
        this.inviteShareService = inviteShareService ?? new DefaultInviteShareService();
        this.shareMessageConfig = shareMessageConfig ?? new ShareMessageConfig(null);
        this.uiStateStore = uiStateStore;
        this.connectInputResolver = connectInputResolver ?? ConnectInputResolverFactory.CreateDefault();
        this.bootstrapHelperIdentityResolver = bootstrapHelperIdentityResolver ?? NknLocalPeerAddressResolver.ResolveAsync;
        RefreshAutomaticIdentityRecoveryWarning();
        this.approvalTimeout = approvalTimeout ?? DefaultApprovalTimeout;
        this.connectFailureCooldown = connectFailureCooldown ?? DefaultConnectFailureCooldown;
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
        lastObservedUiPhase = uiStateStore?.Phase ?? SessionUiPhase.Idle;
        fallbackUiPhase = lastObservedUiPhase;
        ScreenShareViewer = new ScreenShareViewerViewModel();
        remoteControlStateSnapshotTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(FeatureFlags.RemoteControlStateSnapshotIntervalMs),
        };
        remoteControlStateSnapshotTimer.Tick += OnRemoteControlStateSnapshotTimerTick;
        lastKnownShowRemoteScreenShareFrame = ShowRemoteScreenShareFrame;
        lastKnownShowHelperMainContent = ShowHelperMainContent;
        lastKnownHeaderStatusText = HeaderStatusText;

        ChatMessages = new ObservableCollection<ChatLineViewModel>();

        sessionRuntime.StateChanged += OnSessionRuntimeStateChanged;
        sessionRuntime.SessionSecurityStateChanged += OnSessionSecurityStateChanged;
        sessionRuntime.TransientStatusChanged += OnTransientStatusChanged;
        sessionRuntime.Approved += OnApproved;
        sessionRuntime.Rejected += OnRejected;
        sessionRuntime.Disconnected += OnDisconnected;
        sessionRuntime.RemoteSessionEnded += OnRemoteSessionEnded;
        sessionRuntime.ScreenShareFrameCompleted += OnScreenShareFrameCompleted;
        sessionRuntime.ScreenShareStopped += OnScreenShareStopped;
        sessionRuntime.ChatMessageReceived += OnChatMessageReceived;
        sessionRuntime.ChatMessageReceivedBeforeApproved += OnChatMessageReceivedBeforeApproved;
        sessionRuntime.ChatStateChanged += OnChatStateChanged;
        sessionRuntime.FileTransferChanged += OnFileTransferChanged;
        sessionRuntime.RemoteControlStateChanged += OnRemoteControlStateChanged;
        this.statusPresenter.StatusChanged += OnStatusPresenterChanged;
        copyFeedback.PropertyChanged += OnCopyFeedbackPropertyChanged;
        ScreenShareViewer.PropertyChanged += OnScreenShareViewerPropertyChanged;
        if (this.uiStateStore is not null)
        {
            this.uiStateStore.PropertyChanged += OnUiStateStorePropertyChanged;
        }

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, CanConnect);
        CopyHelperIdentityCommand = new AsyncRelayCommand(CopyHelperIdentityAsync);
        ShareHelperIdentityCommand = new AsyncRelayCommand(ShareHelperIdentityAsync);
        CopyInstallMessageCommand = new AsyncRelayCommand(CopyInstallMessageAsync);
        SendFileCommand = new RelayCommand(RequestSendFileWindow, CanExecuteSendFileAction);
        SendChatCommand = new AsyncRelayCommand(
            SendChatAsync,
            CanSendChat,
            AsyncRelayCommandOptions.AllowConcurrentExecutions);
        AcceptIncomingFileCommand = new AsyncRelayCommand<string?>(AcceptIncomingFileAsync, CanAcceptIncomingFile);
        DeclineIncomingFileCommand = new AsyncRelayCommand<string?>(DeclineIncomingFileAsync, CanDeclineIncomingFile);
        CancelFileTransferCommand = new AsyncRelayCommand<string?>(CancelFileTransferAsync, CanCancelFileTransfer);
        RetryCommand = new AsyncRelayCommand(RetryAsync, CanRetry);
        CancelTransientCommand = new AsyncRelayCommand(CancelTransientAsync, CanCancelTransientOperation);
        OpenDiagnosticsCommand = new RelayCommand(OpenDiagnostics, CanOpenDiagnosticsCommand);
        CancelCommand = new RelayCommand(CancelAndGoBack);
        EndSessionCommand = new RelayCommand(EndSession, CanTriggerEndSession);
        ScanQrFromFileCommand = new RelayCommand(RequestScanQrFromFile, () => ShowMainControls);
        ScanQrFromCameraCommand = new RelayCommand(RequestScanQrFromCamera, () => ShowMainControls);
        RequestControlCommand = new RelayCommand(RequestRemoteControl, CanRequestRemoteControlAction);
        StopControlCommand = new RelayCommand(StopRemoteControl, CanStopRemoteControlAction);
        ToggleControlModeCommand = new RelayCommand(ToggleControlMode, CanToggleControlModeAction);
        ToggleRemoteControlDebugPanelCommand = new RelayCommand(ToggleRemoteControlDebugPanel, CanToggleRemoteControlDebugPanel);

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
        BeginBootstrapHelperIdentityResolution();
    }

    public string CodeInput
    {
        get => codeInput;
        set
        {
            var normalized = NormalizeConnectInputForDisplay(value ?? string.Empty);
            if (SetProperty(ref codeInput, normalized))
            {
                UpdatePreviewInviteBoundHelperIdentity(normalized);
                ConnectCommand.NotifyCanExecuteChanged();
                SendChatCommand.NotifyCanExecuteChanged();
                ScanQrFromFileCommand.NotifyCanExecuteChanged();
                ScanQrFromCameraCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(ShowChatPanel));
                OnPropertyChanged(nameof(ShowChatConnectionHint));
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
                OnPropertyChanged(nameof(ShowTransientStatusPanel));
                OnPropertyChanged(nameof(ShowRequestControlAction));
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
                OnPropertyChanged(nameof(ShowHelperIdentityBootstrapPanel));
                OnPropertyChanged(nameof(HelperIdentityBootstrapHintText));
                OnPropertyChanged(nameof(HeaderVerificationCodeText));
                OnPropertyChanged(nameof(ShowHeaderVerificationCode));
                OnPropertyChanged(nameof(FirstPillVerificationCodeText));
                OnPropertyChanged(nameof(ShowFirstPillVerificationCode));
                OnPropertyChanged(nameof(ShowBackButton));
                OnPropertyChanged(nameof(HeaderStatusText));
                OnPropertyChanged(nameof(ShowTransientStatusPanel));
                OnPropertyChanged(nameof(ShowRequestControlAction));
                OnPropertyChanged(nameof(CanRequestControl));
                OnPropertyChanged(nameof(ShowStopControlAction));
                OnPropertyChanged(nameof(CanStopControl));
                OnPropertyChanged(nameof(IsRemoteControlInputCaptureEnabled));
                OnPropertyChanged(nameof(ShowControlModeToggle));
                OnPropertyChanged(nameof(CanControlModeToggle));
                OnPropertyChanged(nameof(ControlModeButtonText));
                OnPropertyChanged(nameof(IsRemoteControlKeyboardCaptureEnabled));
                NotifyRemoteControlDiagnosticsChanged();
                RequestControlCommand.NotifyCanExecuteChanged();
                StopControlCommand.NotifyCanExecuteChanged();
                ToggleControlModeCommand.NotifyCanExecuteChanged();
                ScanQrFromFileCommand.NotifyCanExecuteChanged();
                ScanQrFromCameraCommand.NotifyCanExecuteChanged();
                EnsureControlModeConsistency();
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

    public string HelperPageHelpText => "Paste invite or invite code.";

    public string ConnectionMethodHint => transportConfig.HelperHintText;

    private PeerAddress? HelperVerificationIdentity => sessionRuntime.SecurityState.HelperAddress;
    private PeerAddress? HelperIdentityForInviteBinding => bootstrapHelperIdentity ?? sessionRuntime.CurrentLocalPeerAddress;
    private bool RequiresHelperIdentityBootstrap =>
        string.Equals(transportConfig.Key, "NKN", StringComparison.OrdinalIgnoreCase) &&
        InviteSecurityDiagnostics.RequiresBoundHelperForIssuedSecretInvites();

    public string HelperIdentityBootstrapText =>
        HelperIdentityForInviteBinding is { } helperIdentity
            ? helperIdentity.Value
            : string.Empty;

    public string HelperIdentityBootstrapHintText =>
        !string.IsNullOrWhiteSpace(helperIdentityBootstrapErrorText)
            ? helperIdentityBootstrapErrorText
            : !string.IsNullOrWhiteSpace(HelperIdentityBootstrapText) &&
              !string.IsNullOrWhiteSpace(automaticIdentityRecoveryWarning)
            ? $"{automaticIdentityRecoveryWarning} Copy this helper address into the helpee's helper field before they generate the invite."
            : string.IsNullOrWhiteSpace(HelperIdentityBootstrapText)
            ? "Preparing your helper address. Share it with the helpee before they generate the invite."
            : "Copy this helper address into the helpee's helper field before they generate the invite.";

    public string HelperIdentityBootstrapVerificationCode =>
        HelperVerificationCodeFormatter.FormatOrNull(HelperIdentityForInviteBinding) ?? string.Empty;

    public bool HasHelperIdentityBootstrapVerificationCode =>
        !string.IsNullOrWhiteSpace(HelperIdentityBootstrapVerificationCode);

    public bool ShowHelperIdentityBootstrapPanel =>
        ShowMainControls &&
        RequiresHelperIdentityBootstrap &&
        (helperIdentityBootstrapPending ||
         !string.IsNullOrWhiteSpace(HelperIdentityBootstrapText) ||
         !string.IsNullOrWhiteSpace(helperIdentityBootstrapErrorText));

    public string HelperVerificationCode =>
        HelperVerificationCodeFormatter.FormatOrNull(HelperVerificationIdentity) ?? string.Empty;

    public bool HasHelperVerificationCode => !string.IsNullOrWhiteSpace(HelperVerificationCode);

    public bool ShowHelperVerificationCode =>
        HasHelperVerificationCode &&
        EffectivePhase is SessionUiPhase.Connecting or SessionUiPhase.Recovering or SessionUiPhase.Failed;

    public string HeaderVerificationCodeText =>
        (EffectivePhase is SessionUiPhase.Connecting or SessionUiPhase.Failed) && HasHelperIdentityBootstrapVerificationCode
            ? HelperIdentityBootstrapVerificationCode
            : ShowHelperIdentityBootstrapPanel && HasHelperIdentityBootstrapVerificationCode
            ? HelperIdentityBootstrapVerificationCode
            : ShowHelperVerificationCode
                ? HelperVerificationCode
                : string.Empty;

    public bool ShowHeaderVerificationCode =>
        !ShowHelperIdentityBootstrapPanel &&
        !string.IsNullOrWhiteSpace(HeaderVerificationCodeText);

    public string FirstPillVerificationCodeText =>
        HasHelperIdentityBootstrapVerificationCode
            ? HelperIdentityBootstrapVerificationCode
            : string.Empty;

    public bool ShowFirstPillVerificationCode =>
        ShowHelperIdentityBootstrapPanel &&
        !string.IsNullOrWhiteSpace(FirstPillVerificationCodeText);

    public string HelperTechnicalIdentityText => HelperVerificationIdentity?.Value ?? string.Empty;

    public string HelperTechnicalSessionIdText =>
        sessionRuntime.SecurityState.SessionId is SessionId sessionId
            ? $"Session {sessionId.Value}"
            : string.Empty;

    public bool HasHelperTechnicalDetails =>
        !string.IsNullOrWhiteSpace(HelperTechnicalIdentityText) ||
        !string.IsNullOrWhiteSpace(HelperTechnicalSessionIdText);

    public ObservableCollection<ChatLineViewModel> ChatMessages { get; }

    public bool IsConnectedView => ConnectionState == "Connected";

    private bool IsRemoteControlUiConnected =>
        EffectivePhase == SessionUiPhase.Connected &&
        sessionRuntime.State == SessionRuntimeState.Connected;

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
                OnPropertyChanged(nameof(ShowHelperIdentityBootstrapPanel));
                OnPropertyChanged(nameof(HelperIdentityBootstrapHintText));
                OnPropertyChanged(nameof(HeaderVerificationCodeText));
                OnPropertyChanged(nameof(ShowHeaderVerificationCode));
                OnPropertyChanged(nameof(FirstPillVerificationCodeText));
                OnPropertyChanged(nameof(ShowFirstPillVerificationCode));
                ConnectCommand.NotifyCanExecuteChanged();
                RetryCommand.NotifyCanExecuteChanged();
                OpenDiagnosticsCommand.NotifyCanExecuteChanged();
                ScanQrFromFileCommand.NotifyCanExecuteChanged();
                ScanQrFromCameraCommand.NotifyCanExecuteChanged();
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

    public bool ShowChatTopBar => !FeatureFlags.EnableSessionHeader;

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
        TryGetRemoteControlHeaderHint(out var remoteControlHint)
            ? remoteControlHint
            : EffectivePhase switch
            {
                SessionUiPhase.Connecting => "Connecting…",
                SessionUiPhase.Recovering => "Reconnecting…",
                SessionUiPhase.Connected when sessionRuntime.ControlState == ControlState.Active && !sessionRuntime.RemoteControlMappingAvailable
                    => "Remote control mapping unavailable",
                SessionUiPhase.Connected => sessionRuntime.ControlState == ControlState.Requesting && ShowRemoteScreenShareFrame
                    ? "Waiting for approval…"
                    : "Connected",
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
                OnPropertyChanged(nameof(ShowTransientStatusPanel));
                OnPropertyChanged(nameof(ShowRequestControlAction));
                OnPropertyChanged(nameof(CanRequestControl));
                OnPropertyChanged(nameof(ShowStopControlAction));
                OnPropertyChanged(nameof(CanStopControl));
                OnPropertyChanged(nameof(ShowRemoteControlActiveStatus));
                OnPropertyChanged(nameof(IsRemoteControlInputCaptureEnabled));
                OnPropertyChanged(nameof(ShowControlModeToggle));
                OnPropertyChanged(nameof(CanControlModeToggle));
                OnPropertyChanged(nameof(IsRemoteControlKeyboardCaptureEnabled));
                OnPropertyChanged(nameof(ShowRemoteControlDebugToggle));
                OnPropertyChanged(nameof(ShowRemoteControlDebugPanel));
                OnPropertyChanged(nameof(HeaderVerificationCodeText));
                OnPropertyChanged(nameof(ShowHeaderVerificationCode));
                OnPropertyChanged(nameof(FirstPillVerificationCodeText));
                OnPropertyChanged(nameof(ShowFirstPillVerificationCode));
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
        get => canEndSession && sessionRuntime.ControlState != ControlState.Active;
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
        private set
        {
            if (SetProperty(ref canSendFiles, value))
            {
                OnPropertyChanged(nameof(CanSendFileAction));
            }
        }
    }

    public bool SessionSupportsRemoteControl => sessionRuntime.SessionSupportsRemoteControl;
    public bool RemoteControlMappingAvailable => sessionRuntime.RemoteControlMappingAvailable;
    public bool ShowRequestControlAction =>
        IsRemoteControlUiConnected &&
        sessionRuntime.RemoteControlAvailable &&
        ShowRemoteScreenShareFrame &&
        sessionRuntime.ControlState == ControlState.Off;
    public bool CanRequestControl =>
        sessionRuntime.RemoteControlAvailable &&
        IsRemoteControlUiConnected &&
        ShowRemoteScreenShareFrame &&
        sessionRuntime.ControlState == ControlState.Off;
    public bool ShowStopControlAction =>
        IsRemoteControlUiConnected &&
        ShowRemoteScreenShareFrame &&
        sessionRuntime.ControlState is ControlState.Requesting or ControlState.Active;
    public string StopControlButtonText => sessionRuntime.ControlState == ControlState.Requesting ? "Cancel request" : "Stop control";
    public bool CanStopControl =>
        SessionSupportsRemoteControl &&
        IsRemoteControlUiConnected &&
        ShowRemoteScreenShareFrame &&
        sessionRuntime.ControlState is ControlState.Requesting or ControlState.Active;
    public bool ShowRemoteControlActiveStatus =>
        IsRemoteControlUiConnected &&
        sessionRuntime.ControlState == ControlState.Active;
    public bool ShowControlModeToggle => IsRemoteControlKeyboardControlAvailable;
    public bool CanControlModeToggle => ShowControlModeToggle;
    public string ControlModeButtonText => controlModeEnabled ? "Keyboard to remote: On" : "Keyboard to remote: Off";
    public int RemoteControlMouseMoveRateHz => 90;
    public bool ShowRemoteControlDebugToggle =>
        RemoteControlDebugPanelEnabled &&
        IsRemoteControlUiConnected &&
        ShowRemoteScreenShareFrame;
    public bool ShowRemoteControlDebugPanel =>
        ShowRemoteControlDebugToggle &&
        remoteControlDebugPanelExpanded;
    public string RemoteControlDebugToggleText => ShowRemoteControlDebugPanel ? "Hide diagnostics" : "Show diagnostics";
    public string RemoteControlDiagnosticsRoleText => "Helper";
    public string RemoteControlDiagnosticsControlStateText => sessionRuntime.ControlState.ToString();
    public string RemoteControlDiagnosticsControlModeText => controlModeEnabled ? "On" : "Off";
    public string RemoteControlDiagnosticsDisplayText =>
        sessionRuntime.RemoteControlMappingDisplayId is { Length: > 0 } displayId
            ? $"{displayId}@{sessionRuntime.RemoteControlMappingRevision?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}"
            : "n/a";
    public string RemoteControlDiagnosticsCaptureFrameText => FormatCaptureFrameText(GetRemoteControlDiagnosticsSnapshot());
    public string RemoteControlDiagnosticsMoveStatsText => FormatMoveStatsText(GetRemoteControlDiagnosticsSnapshot());
    public string RemoteControlDiagnosticsSuppressionsText => FormatSuppressionText(GetRemoteControlDiagnosticsSnapshot());
    public string RemoteControlDiagnosticsLastMappedText => FormatLastMappedText(GetRemoteControlDiagnosticsSnapshot());
    public string RemoteControlDebugControlStateText => RemoteControlDiagnosticsControlStateText;
    public string RemoteControlDebugDisplayRevisionText =>
        sessionRuntime.RemoteControlMappingRevision?.ToString(CultureInfo.InvariantCulture) ?? "n/a";
    public string RemoteControlDebugRequestIdText => sessionRuntime.CurrentControlRequestId ?? "n/a";
    public string RemoteControlDebugControllerPeerText => sessionRuntime.ControllerPeerId ?? "n/a";
    public string RemoteControlDebugMappingDisplayText => RemoteControlDiagnosticsDisplayText;
    public string RemoteControlDebugControlModeText => RemoteControlDiagnosticsControlModeText;
    public string RemoteControlDebugMouseMoveRateText => $"{remoteControlDebugMouseMovesPerSecond}/s";
    public string RemoteControlDebugGuardrailCountersText =>
        $"clamps={sessionRuntime.RemoteControlDebugMappingClampCount}; drops={sessionRuntime.RemoteControlDebugQueueDropCount}; suppressed={sessionRuntime.RemoteControlDebugInjectionSuppressedCount}; flushes={sessionRuntime.RemoteControlDebugQueueFlushCount}";
#if DEBUG
    internal RemoteControlDebugSnapshot RemoteControlDiagnosticsSnapshotForDebug =>
        RemoteControlDebugDiagnostics.Snapshot(RemoteControlDiagnosticsRole.Helper);
#endif
    public bool IsRemoteControlInputCaptureEnabled =>
        sessionRuntime.RemoteControlAvailable &&
        RemoteControlMappingAvailable &&
        IsRemoteControlUiConnected &&
        ShowRemoteScreenShareFrame &&
        sessionRuntime.ControlState == ControlState.Active;
    private bool IsRemoteControlKeyboardControlAvailable =>
        sessionRuntime.RemoteControlAvailable &&
        IsRemoteControlUiConnected &&
        ShowRemoteScreenShareFrame &&
        sessionRuntime.ControlState == ControlState.Active;
    public bool IsRemoteControlKeyboardCaptureEnabled =>
        IsRemoteControlKeyboardControlAvailable &&
        controlModeEnabled;

    public bool IsChatInputEnabled
    {
        get => isChatInputEnabled;
        private set => SetProperty(ref isChatInputEnabled, value);
    }

    public bool ShowSendFileAction =>
        EffectivePhase == SessionUiPhase.Connected &&
        sessionRuntime.CanPerform(SessionCapability.FileTransfer);

    public bool CanSendFileAction => CanSendFiles;

    public FileTransferPanelItemViewModel? InboundFileTransfer
    {
        get => inboundFileTransfer;
        private set => SetProperty(ref inboundFileTransfer, value);
    }

    public FileTransferPanelItemViewModel? OutboundFileTransfer
    {
        get => outboundFileTransfer;
        private set => SetProperty(ref outboundFileTransfer, value);
    }

    public IAsyncRelayCommand ConnectCommand { get; }

    public IAsyncRelayCommand CopyHelperIdentityCommand { get; }

    public IAsyncRelayCommand ShareHelperIdentityCommand { get; }

    public IAsyncRelayCommand CopyInstallMessageCommand { get; }

    public IRelayCommand SendFileCommand { get; }

    public IAsyncRelayCommand SendChatCommand { get; }

    public IAsyncRelayCommand<string?> AcceptIncomingFileCommand { get; }

    public IAsyncRelayCommand<string?> DeclineIncomingFileCommand { get; }

    public IAsyncRelayCommand<string?> CancelFileTransferCommand { get; }

    public IAsyncRelayCommand RetryCommand { get; }
    public IAsyncRelayCommand CancelTransientCommand { get; }

    public IRelayCommand OpenDiagnosticsCommand { get; }

    public IRelayCommand CancelCommand { get; }
    public IRelayCommand EndSessionCommand { get; }
    public IRelayCommand ScanQrFromFileCommand { get; }
    public IRelayCommand ScanQrFromCameraCommand { get; }
    public IRelayCommand RequestControlCommand { get; }
    public IRelayCommand StopControlCommand { get; }
    public IRelayCommand ToggleControlModeCommand { get; }
    public IRelayCommand ToggleRemoteControlDebugPanelCommand { get; }
    public IRelayCommand StatusBannerCopyDiagnosticsCommand => OpenDiagnosticsCommand;
    public IAsyncRelayCommand StatusBannerCancelCommand => CancelTransientCommand;

    public event EventHandler? SendFileRequested;
    public event EventHandler? RemoteControlViewerFocusRequested;
    public event EventHandler? ScanQrFromFileRequested;
    public event EventHandler? ScanQrFromCameraRequested;

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
        CancelBootstrapHelperIdentityResolution();

        remoteControlStateSnapshotTimer.Stop();
        remoteControlStateSnapshotTimer.Tick -= OnRemoteControlStateSnapshotTimerTick;

        sessionRuntime.StateChanged -= OnSessionRuntimeStateChanged;
        sessionRuntime.SessionSecurityStateChanged -= OnSessionSecurityStateChanged;
        sessionRuntime.TransientStatusChanged -= OnTransientStatusChanged;
        sessionRuntime.Approved -= OnApproved;
        sessionRuntime.Rejected -= OnRejected;
        sessionRuntime.Disconnected -= OnDisconnected;
        sessionRuntime.RemoteSessionEnded -= OnRemoteSessionEnded;
        sessionRuntime.ScreenShareFrameCompleted -= OnScreenShareFrameCompleted;
        sessionRuntime.ScreenShareStopped -= OnScreenShareStopped;
        sessionRuntime.ChatMessageReceived -= OnChatMessageReceived;
        sessionRuntime.ChatMessageReceivedBeforeApproved -= OnChatMessageReceivedBeforeApproved;
        sessionRuntime.ChatStateChanged -= OnChatStateChanged;
        sessionRuntime.FileTransferChanged -= OnFileTransferChanged;
        sessionRuntime.RemoteControlStateChanged -= OnRemoteControlStateChanged;
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
        RunBoundedSynchronousCleanup(() => sessionRuntime.DisconnectAsync(), DisposeOperationTimeout);

        connectCts?.Cancel();
        connectCts?.Dispose();
        connectCts = null;
        ScreenShareViewer.Dispose();
        copyFeedback.Dispose();
    }

    public async Task PrepareForWindowCloseAsync()
    {
        if (disposed)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref windowCloseDisconnectStarted, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await sessionRuntime.DisconnectAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort close path. Main disposal still proceeds.
        }
    }

    private bool CanConnect()
    {
        return CanStartOrConnect && !IsStartupBlocked && !IsConnecting && ResolveConnectInput(CodeInput).IsValid;
    }

    private ConnectInputResolution ResolveConnectInput()
    {
        return ResolveConnectInput(CodeInput);
    }

    private ConnectInputResolution ResolveConnectInput(string rawInput)
    {
        return connectInputResolver.Resolve(rawInput, nowProvider());
    }

    private void UpdatePreviewInviteBoundHelperIdentity(string rawInput)
    {
        var resolution = ResolveConnectInput(rawInput);
        var nextPreviewIdentity =
            resolution.Kind == ConnectInputKind.InviteToken
                ? resolution.Invite?.BoundHelperAddress
                : null;
        if (previewInviteBoundHelperIdentity == nextPreviewIdentity)
        {
            return;
        }

        previewInviteBoundHelperIdentity = nextPreviewIdentity;
        OnPropertyChanged(nameof(HeaderVerificationCodeText));
        OnPropertyChanged(nameof(ShowHeaderVerificationCode));
        OnPropertyChanged(nameof(FirstPillVerificationCodeText));
        OnPropertyChanged(nameof(ShowFirstPillVerificationCode));
    }

    private static string NormalizeConnectInputForDisplay(string incoming)
    {
        return incoming;
    }

    private void RequestScanQrFromFile()
    {
        if (!ShowMainControls)
        {
            return;
        }

        ScanQrFromFileRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RequestScanQrFromCamera()
    {
        if (!ShowMainControls)
        {
            return;
        }

        ScanQrFromCameraRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ApplyExternalConnectInput(string input, string sourceLabel)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        CodeInput = input.Trim();
        copyFeedback.Show(sourceLabel switch
        {
            "clipboard" => "Pasted from clipboard.",
            "qr" => "Scanned from QR code.",
            _ => "Added."
        });
    }

    public void NotifyExternalInputError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        copyFeedback.Show(message);
    }

    public void NotifySendFileError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        copyFeedback.Show(message);
    }

    public async Task StartSendFileAsync(
        FileTransferSendDescriptor descriptor,
        FileTransferReadStreamFactory openReadStreamAsync,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(openReadStreamAsync);

        var snapshot = await sessionRuntime.StartSendAsync(descriptor, openReadStreamAsync, ct);
        if (snapshot is null)
        {
            copyFeedback.Show("Couldn't start the file transfer.");
        }
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
        CancelBootstrapHelperIdentityResolution();
        await AwaitBootstrapHelperIdentityResolutionCompletionAsync();
        var connectInputText = CodeInput;
        PrepareForNewSession(clearConnectInput: false);
        uiRecoveryTransientDismissed = false;

        if (IsStartupBlocked)
        {
            return;
        }

        if (IsInFailureCooldown())
        {
            return;
        }

        var connectInput = ResolveConnectInput(connectInputText);
        if (!connectInput.IsValid)
        {
            LogInvalidConnectInput(connectInput);
            StatusText = connectInput.Message ?? UserErrorMapper.HelperInvalidConnectInput();
            ConnectionState = "InvalidInput";
            OnPropertyChanged(nameof(ShowChatConnectionHint));
            return;
        }

        if (connectInput.Kind == ConnectInputKind.PeerAddress)
        {
            var rawTarget = connectInput.TargetAddress?.Value ?? connectInputText.Trim();
            AppLog.Info($"Helper join rejected using {transportConfig.Key}; reason=invite_required; target={rawTarget}");
            StatusText = UserErrorMapper.HelperInviteRequired();
            ConnectionState = "InvalidInput";
            OnPropertyChanged(nameof(ShowChatConnectionHint));
            return;
        }

        if (connectInput.Kind != ConnectInputKind.InviteToken ||
            connectInput.Invite is null ||
            connectInput.TargetAddress is not PeerAddress targetAddress)
        {
            StatusText = UserErrorMapper.HelperInvalidConnectInput();
            ConnectionState = "InvalidInput";
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

        IsConnecting = true;
        StatusText = "Connecting…";
        ConnectionState = "Connecting";
        OnPropertyChanged(nameof(ShowChatConnectionHint));
        connectOutcome = new TaskCompletionSource<HelperConnectOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var inviteExpiry = connectInput.Invite.Payload.ExpiresAtUtcMs.ToString(CultureInfo.InvariantCulture);
            AppLog.Info($"Helper join requested using {transportConfig.Key} with prechecked_invite target {targetAddress.Value}; invite_exp_utc_ms={inviteExpiry}");
            var inviteTokenInput = connectInput.InviteTokenText ?? connectInputText.Trim();
            await sessionRuntime.StartHelperAsync(inviteTokenInput, connectInput.Invite, connectCts.Token);

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
            LogReliability(SessionReliabilityStage.DiscoveryTimeout, "timeout", "No response from target.");
            var snapshot = NknRuntimeDiagnostics.Snapshot();
            var failure = TransportFailureMapper.FromException(ex, snapshot.LastError, snapshot.LastDisconnectReason);
            var uiMessage = UserErrorMapper.FromHelperTimeoutException(ex);
            await sessionRuntime.FailAsync(failure, uiMessage);
            MarkFailedAttemptNow();
            OnPropertyChanged(nameof(ShowChatConnectionHint));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Helper connect failed before approval ({ex.GetType().Name}): {ex.Message}");
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
            ScanQrFromFileCommand.NotifyCanExecuteChanged();
            ScanQrFromCameraCommand.NotifyCanExecuteChanged();
        });
    }

    private async Task SendChatAsync()
    {
        if (disposed)
        {
            return;
        }

        if (!sessionRuntime.CanSendChat && !IsConnecting && ResolveConnectInput().IsValid)
        {
            await ConnectAsync();
        }

        var draft = ChatDraft;
        var optimisticText = draft.Trim();
        if (string.IsNullOrWhiteSpace(optimisticText))
        {
            return;
        }

        var sendAttempt = Interlocked.Increment(ref chatSendAttemptCounter);
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
        if (sendAttempt == Interlocked.Read(ref chatSendAttemptCounter) &&
            string.IsNullOrWhiteSpace(ChatDraft))
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
        var now = nowProvider();
        if (now < endSessionGuardUntilUtc)
        {
            var remainingMs = Math.Max(0, (int)Math.Ceiling((endSessionGuardUntilUtc - now).TotalMilliseconds));
            AppLog.Info($"Helper end session ignored during post-stop guard window ({remainingMs}ms remaining).");
            return;
        }

        if (sessionRuntime.ControlState == ControlState.Active)
        {
            AppLog.Info("Helper end session ignored while remote control is active.");
            return;
        }

        if (endInvoked)
        {
            return;
        }

        endInvoked = true;
        EndSessionCommand.NotifyCanExecuteChanged();
        endSessionRequested = true;
        endReason = SessionEndReason.UserEnded;
        connectCts?.Cancel();
        connectOutcome?.TrySetCanceled();
        IsConnecting = false;
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
        CodeInput = string.Empty;
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

    private void RequestRemoteControl()
    {
        _ = RequestRemoteControlAsync();
    }

    private void StopRemoteControl()
    {
        endSessionGuardUntilUtc = nowProvider() + EndSessionAfterControlStopGuard;
        _ = StopRemoteControlAsync("helper_stop");
    }

    private async Task RequestRemoteControlAsync()
    {
        if (!CanRequestRemoteControlAction())
        {
            return;
        }

        var ok = await sessionRuntime.DispatchRemoteControlHelperEventAsync(
            RemoteControlReducerEventKind.HelperRequestClicked,
            "helper_request",
            CancellationToken.None);
        if (!ok)
        {
            AppLog.Info("Remote control request failed");
        }
    }

    private static void LogInvalidConnectInput(ConnectInputResolution resolution)
    {
        var reason = resolution.Error switch
        {
            ConnectInputValidationError.ExpiredInviteToken => "expired_invite",
            ConnectInputValidationError.InvalidInviteToken when resolution.InviteValidationError == InviteTokenValidationError.UnsupportedVersion => "invite_version_unsupported",
            ConnectInputValidationError.InvalidInviteToken when resolution.InviteValidationError == InviteTokenValidationError.ParseFailed => "invite_parse_failed",
            ConnectInputValidationError.InvalidInviteToken => "invite_invalid",
            ConnectInputValidationError.Empty => "empty_input",
            ConnectInputValidationError.UnsupportedInput => "unsupported_input",
            ConnectInputValidationError.InvalidAddress => "invalid_address",
            _ => "invalid_input",
        };

        AppLog.Warn(
            $"Helper connect input rejected; reason={reason}; error={resolution.Error}; invite_validation={resolution.InviteValidationError}; invite_parse={resolution.InviteParseError}; message={resolution.Message ?? "(none)"}");
    }

    private async Task StopRemoteControlAsync(string reason)
    {
        if (!CanStopRemoteControlAction())
        {
            return;
        }

        await sessionRuntime.DispatchRemoteControlHelperEventAsync(
            RemoteControlReducerEventKind.HelperStopClicked,
            reason,
            CancellationToken.None);
    }

    public void PostRemoteControlInput(ControlInputMessageV1 message)
    {
        var kind = string.IsNullOrWhiteSpace(message.Kind) ? string.Empty : message.Kind.Trim();
        if (string.Equals(kind, "key", StringComparison.Ordinal))
        {
            if (!IsRemoteControlKeyboardCaptureEnabled)
            {
                return;
            }
        }
        else if (!IsRemoteControlInputCaptureEnabled)
        {
            return;
        }

        TrackRemoteControlDebugMetrics(message);
        _ = sessionRuntime.SendRemoteControlInputAsync(message, CancellationToken.None);
    }

    public void UpdateRemoteControlHeldState(
        RemoteControlModifiersMask modifiersMask,
        RemoteControlMouseButtonsMask mouseButtonsMask,
        bool immediateReleaseAll)
    {
        var buttonsChanged = remoteControlHeldMouseButtonsMask != mouseButtonsMask;
        remoteControlHeldModifiersMask = modifiersMask;
        remoteControlHeldMouseButtonsMask = mouseButtonsMask;
        SyncRemoteControlStateSnapshotPump();

        if (!FeatureFlags.RemoteControlStateSnapshotEnabled)
        {
            return;
        }

        if (immediateReleaseAll || buttonsChanged)
        {
            remoteControlSnapshotImmediateRequested = true;
            _ = TrySendRemoteControlStateSnapshotAsync(forceSend: true);
        }
    }

    public void ExitControlMode()
    {
        SetControlModeEnabled(false);
    }

    private bool CanRequestRemoteControlAction()
    {
        return CanRequestControl;
    }

    private bool CanStopRemoteControlAction()
    {
        return CanStopControl;
    }

    private void ToggleControlMode()
    {
        if (!CanToggleControlModeAction())
        {
            return;
        }

        controlModeEnabled = !controlModeEnabled;
        OnPropertyChanged(nameof(ControlModeButtonText));
        OnPropertyChanged(nameof(RemoteControlDebugControlModeText));
        OnPropertyChanged(nameof(RemoteControlDiagnosticsControlModeText));
        OnPropertyChanged(nameof(IsRemoteControlKeyboardCaptureEnabled));
        ToggleControlModeCommand.NotifyCanExecuteChanged();

        if (controlModeEnabled && IsRemoteControlKeyboardCaptureEnabled)
        {
            RemoteControlViewerFocusRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool CanToggleControlModeAction()
    {
        return CanControlModeToggle;
    }

    private void SetControlModeEnabled(bool enabled)
    {
        if (controlModeEnabled == enabled)
        {
            return;
        }

        controlModeEnabled = enabled;
        OnPropertyChanged(nameof(ControlModeButtonText));
        OnPropertyChanged(nameof(RemoteControlDebugControlModeText));
        OnPropertyChanged(nameof(RemoteControlDiagnosticsControlModeText));
        OnPropertyChanged(nameof(IsRemoteControlKeyboardCaptureEnabled));
        ToggleControlModeCommand.NotifyCanExecuteChanged();
    }

    private void ToggleRemoteControlDebugPanel()
    {
        if (!CanToggleRemoteControlDebugPanel())
        {
            return;
        }

        remoteControlDebugPanelExpanded = !remoteControlDebugPanelExpanded;
        NotifyRemoteControlDiagnosticsChanged();
    }

    private bool CanToggleRemoteControlDebugPanel()
    {
        return ShowRemoteControlDebugToggle;
    }

    private void EnsureControlModeConsistency()
    {
        if (IsRemoteControlKeyboardControlAvailable)
        {
            SyncRemoteControlStateSnapshotPump();
            return;
        }

        SetControlModeEnabled(false);
        ResetRemoteControlDebugMetrics();
        SyncRemoteControlStateSnapshotPump();
    }

    private void OnRemoteControlStateSnapshotTimerTick(object? sender, EventArgs e)
    {
        _ = TrySendRemoteControlStateSnapshotAsync(forceSend: false);
    }

    private void SyncRemoteControlStateSnapshotPump()
    {
        var shouldRun = FeatureFlags.RemoteControlStateSnapshotEnabled &&
                        (IsRemoteControlInputCaptureEnabled || IsRemoteControlKeyboardCaptureEnabled) &&
                        sessionRuntime.ControlState == ControlState.Active;
        if (!shouldRun)
        {
            remoteControlStateSnapshotTimer.Stop();
            remoteControlSnapshotHasSent = false;
            remoteControlLastSnapshotSentTickMs = 0;
            remoteControlSnapshotImmediateRequested = false;
            remoteControlSnapshotSendInProgress = false;
            ResetRemoteControlSnapshotDebugMetrics();
            return;
        }

        if (!remoteControlStateSnapshotTimer.IsEnabled)
        {
            remoteControlStateSnapshotTimer.Start();
            _ = TrySendRemoteControlStateSnapshotAsync(forceSend: true);
        }
    }

    private async Task TrySendRemoteControlStateSnapshotAsync(bool forceSend)
    {
        if (!FeatureFlags.RemoteControlStateSnapshotEnabled)
        {
            return;
        }

        if (remoteControlSnapshotSendInProgress)
        {
            if (forceSend)
            {
                remoteControlSnapshotImmediateRequested = true;
            }
            return;
        }

        if (!IsRemoteControlInputCaptureEnabled ||
            sessionRuntime.ControlState != ControlState.Active)
        {
            return;
        }

        var requestId = sessionRuntime.CurrentControlRequestId;
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return;
        }

        var nowTickMs = Environment.TickCount64;
        var modifiersMask = remoteControlHeldModifiersMask;
        var mouseButtonsMask = remoteControlHeldMouseButtonsMask;
        var masksChanged = !remoteControlSnapshotHasSent ||
                           modifiersMask != remoteControlLastSentModifiersMask ||
                           mouseButtonsMask != remoteControlLastSentMouseButtonsMask;
        var keepAliveDue = !remoteControlSnapshotHasSent ||
                           nowTickMs - remoteControlLastSnapshotSentTickMs >= (long)RemoteControlSnapshotKeepAliveInterval.TotalMilliseconds;
        var shouldSend = forceSend || remoteControlSnapshotImmediateRequested || masksChanged || keepAliveDue;
        if (!shouldSend)
        {
            return;
        }

        var snapshot = new ControlStateSnapshotV1
        {
            RequestId = requestId,
            Seq = Interlocked.Increment(ref remoteControlSnapshotSequence),
            TsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ModifiersMask = (int)modifiersMask,
            MouseButtonsMask = (int)mouseButtonsMask,
        };

        remoteControlSnapshotSendInProgress = true;
        try
        {
            var sent = await sessionRuntime.SendRemoteControlStateSnapshotAsync(snapshot, CancellationToken.None);
            if (!sent)
            {
                return;
            }

            remoteControlSnapshotHasSent = true;
            remoteControlLastSnapshotSentTickMs = Environment.TickCount64;
            remoteControlLastSentModifiersMask = modifiersMask;
            remoteControlLastSentMouseButtonsMask = mouseButtonsMask;
            remoteControlLastSnapshotSentSeq = snapshot.Seq;
            RecordRemoteControlSnapshotSentForDebug();
            remoteControlSnapshotImmediateRequested = false;
        }
        finally
        {
            remoteControlSnapshotSendInProgress = false;
        }
    }

    [Conditional("DEBUG")]
    private void RecordRemoteControlSnapshotSentForDebug()
    {
        var nowMs = Environment.TickCount64;
        if (remoteControlSnapshotSendWindowStartTickMs == 0)
        {
            remoteControlSnapshotSendWindowStartTickMs = nowMs;
        }

        if (nowMs - remoteControlSnapshotSendWindowStartTickMs >= 1000)
        {
            remoteControlSnapshotSendsPerSecond = remoteControlSnapshotSendsInWindow;
            remoteControlSnapshotSendsInWindow = 0;
            remoteControlSnapshotSendWindowStartTickMs = nowMs;
        }

        remoteControlSnapshotSendsInWindow++;
        RemoteControlDebugDiagnostics.SetHelperSnapshotRuntime(
            lastSentSeq: remoteControlLastSnapshotSentSeq,
            lastSentModifiersMask: (int)remoteControlLastSentModifiersMask,
            lastSentMouseButtonsMask: (int)remoteControlLastSentMouseButtonsMask,
            sentPerSec: remoteControlSnapshotSendsPerSecond);
        NotifyRemoteControlDiagnosticsChanged();
    }

    [Conditional("DEBUG")]
    private void ResetRemoteControlSnapshotDebugMetrics()
    {
        remoteControlLastSnapshotSentSeq = 0;
        remoteControlSnapshotSendsPerSecond = 0;
        remoteControlSnapshotSendsInWindow = 0;
        remoteControlSnapshotSendWindowStartTickMs = 0;
        RemoteControlDebugDiagnostics.SetHelperSnapshotRuntime(
            lastSentSeq: 0,
            lastSentModifiersMask: (int)RemoteControlModifiersMask.None,
            lastSentMouseButtonsMask: (int)RemoteControlMouseButtonsMask.None,
            sentPerSec: 0);
        NotifyRemoteControlDiagnosticsChanged();
    }

    [Conditional("DEBUG")]
    private void TrackRemoteControlDebugMetrics(ControlInputMessageV1 message)
    {
        if (!RemoteControlDebugPanelEnabled ||
            !string.Equals(message.Kind, "mouse_move", StringComparison.Ordinal))
        {
            return;
        }

        var nowMs = Environment.TickCount64;
        if (remoteControlDebugMouseMoveWindowStartTickMs == 0)
        {
            remoteControlDebugMouseMoveWindowStartTickMs = nowMs;
        }

        if (nowMs - remoteControlDebugMouseMoveWindowStartTickMs >= 1000)
        {
            remoteControlDebugMouseMovesPerSecond = remoteControlDebugMouseMovesInWindow;
            remoteControlDebugMouseMovesInWindow = 0;
            remoteControlDebugMouseMoveWindowStartTickMs = nowMs;
            OnPropertyChanged(nameof(RemoteControlDebugMouseMoveRateText));
            OnPropertyChanged(nameof(RemoteControlDiagnosticsMoveStatsText));
        }

        remoteControlDebugMouseMovesInWindow++;
    }

    [Conditional("DEBUG")]
    private void ResetRemoteControlDebugMetrics()
    {
        remoteControlDebugMouseMoveWindowStartTickMs = 0;
        remoteControlDebugMouseMovesInWindow = 0;
        if (remoteControlDebugMouseMovesPerSecond == 0)
        {
            return;
        }

        remoteControlDebugMouseMovesPerSecond = 0;
        OnPropertyChanged(nameof(RemoteControlDebugMouseMoveRateText));
        OnPropertyChanged(nameof(RemoteControlDiagnosticsMoveStatsText));
    }

    private void RequestSendFileWindow()
    {
        if (!CanExecuteSendFileAction())
        {
            return;
        }

        if (!sessionRuntime.TryAuthorizeFileTransferSend())
        {
            CanSendFiles = false;
            OnPropertyChanged(nameof(CanSendFileAction));
            SendFileCommand.NotifyCanExecuteChanged();
            return;
        }

        SendFileRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool CanExecuteSendFileAction()
    {
        return CanSendFiles;
    }

    private async Task AcceptIncomingFileAsync(string? transferId)
    {
        var normalizedTransferId = NormalizeTransferActionId(transferId);
        if (!CanAcceptIncomingFile(normalizedTransferId))
        {
            return;
        }

        await sessionRuntime.AcceptIncomingAsync(normalizedTransferId!, CancellationToken.None).ConfigureAwait(false);
    }

    private bool CanAcceptIncomingFile(string? transferId)
    {
        var normalizedTransferId = NormalizeTransferActionId(transferId);
        return normalizedTransferId is not null &&
               InboundFileTransfer is { ShowAccept: true } inbound &&
               string.Equals(inbound.TransferId, normalizedTransferId, StringComparison.Ordinal);
    }

    private async Task DeclineIncomingFileAsync(string? transferId)
    {
        var normalizedTransferId = NormalizeTransferActionId(transferId);
        if (!CanDeclineIncomingFile(normalizedTransferId))
        {
            return;
        }

        await sessionRuntime.DeclineIncomingAsync(normalizedTransferId!, uiCt: CancellationToken.None).ConfigureAwait(false);
    }

    private bool CanDeclineIncomingFile(string? transferId)
    {
        var normalizedTransferId = NormalizeTransferActionId(transferId);
        return normalizedTransferId is not null &&
               InboundFileTransfer is { ShowDecline: true } inbound &&
               string.Equals(inbound.TransferId, normalizedTransferId, StringComparison.Ordinal);
    }

    private async Task CancelFileTransferAsync(string? transferId)
    {
        var normalizedTransferId = NormalizeTransferActionId(transferId);
        if (!CanCancelFileTransfer(normalizedTransferId))
        {
            return;
        }

        await sessionRuntime.CancelTransferAsync(normalizedTransferId!, uiCt: CancellationToken.None).ConfigureAwait(false);
    }

    private bool CanCancelFileTransfer(string? transferId)
    {
        var normalizedTransferId = NormalizeTransferActionId(transferId);
        if (normalizedTransferId is null)
        {
            return false;
        }

        return (InboundFileTransfer is { ShowCancel: true } inbound &&
                string.Equals(inbound.TransferId, normalizedTransferId, StringComparison.Ordinal)) ||
               (OutboundFileTransfer is { ShowCancel: true } outbound &&
                string.Equals(outbound.TransferId, normalizedTransferId, StringComparison.Ordinal));
    }

    private static string? NormalizeTransferActionId(string? transferId)
    {
        return string.IsNullOrWhiteSpace(transferId) ? null : transferId.Trim();
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

    private async Task CopyHelperIdentityAsync()
    {
        if (clipboardService is null)
        {
            copyFeedback.Show("Could not copy. Please try again.");
            return;
        }

        if (string.IsNullOrWhiteSpace(HelperIdentityBootstrapText))
        {
            copyFeedback.Show("Helper address is not ready yet.");
            return;
        }

        try
        {
            await clipboardService.SetTextAsync(HelperIdentityBootstrapText);
            copyFeedback.Show("Helper address copied.");
        }
        catch
        {
            copyFeedback.Show("Could not copy. Please try again.");
        }
    }

    private async Task ShareHelperIdentityAsync()
    {
        if (string.IsNullOrWhiteSpace(HelperIdentityBootstrapText))
        {
            copyFeedback.Show("Helper address is not ready yet.");
            return;
        }

        try
        {
            var shared = await inviteShareService.ShareInviteAsync(HelperIdentityBootstrapText, CancellationToken.None);
            copyFeedback.Show(shared.IsSuccess
                ? "Helper address shared."
                : shared.Message ?? "Could not share. Please try again.");
        }
        catch
        {
            copyFeedback.Show("Could not share. Please try again.");
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

    private void BeginBootstrapHelperIdentityResolution()
    {
        if (!RequiresHelperIdentityBootstrap)
        {
            return;
        }

        CancelBootstrapHelperIdentityResolution();
        helperIdentityBootstrapErrorText = string.Empty;
        bootstrapHelperIdentityResolutionCts = new CancellationTokenSource();
        helperIdentityBootstrapPending = true;
        bootstrapHelperIdentityResolutionTask = ResolveBootstrapHelperIdentityAsync(bootstrapHelperIdentityResolutionCts.Token);
    }

    private void NotifyHelperIdentityBootstrapChanged()
    {
        OnPropertyChanged(nameof(HelperIdentityBootstrapText));
        OnPropertyChanged(nameof(HelperIdentityBootstrapHintText));
        OnPropertyChanged(nameof(HelperIdentityBootstrapVerificationCode));
        OnPropertyChanged(nameof(HasHelperIdentityBootstrapVerificationCode));
        OnPropertyChanged(nameof(ShowHelperIdentityBootstrapPanel));
        OnPropertyChanged(nameof(HeaderVerificationCodeText));
        OnPropertyChanged(nameof(ShowHeaderVerificationCode));
        OnPropertyChanged(nameof(FirstPillVerificationCodeText));
        OnPropertyChanged(nameof(ShowFirstPillVerificationCode));
    }

    private void PromoteBootstrapHelperIdentityFromConnectedSessionIfAvailable()
    {
        if (sessionRuntime.State != SessionRuntimeState.Connected ||
            sessionRuntime.SecurityState.HelperAddress is not { } verifiedIdentity)
        {
            return;
        }

        var changed =
            bootstrapHelperIdentity is null ||
            bootstrapHelperIdentity.Value != verifiedIdentity;

        bootstrapHelperIdentity = verifiedIdentity;
        helperIdentityBootstrapErrorText = string.Empty;
        helperIdentityBootstrapPending = false;
        CancelBootstrapHelperIdentityResolution();
        bootstrapHelperIdentityResolutionTask = null;

        if (changed)
        {
            NotifyHelperIdentityBootstrapChanged();
        }
    }

    private void CacheBootstrapHelperIdentityFromRuntimeIfAvailable()
    {
        if (bootstrapHelperIdentity is not null)
        {
            return;
        }

        var resolvedIdentity = sessionRuntime.CurrentLocalPeerAddress;
        if (resolvedIdentity is null)
        {
            return;
        }

        bootstrapHelperIdentity = resolvedIdentity;
        helperIdentityBootstrapErrorText = string.Empty;
        helperIdentityBootstrapPending = false;
        CancelBootstrapHelperIdentityResolution();
        bootstrapHelperIdentityResolutionTask = null;
        NotifyHelperIdentityBootstrapChanged();
    }

    private void EnsureBootstrapHelperIdentityResolutionForReadyState()
    {
        if (!RequiresHelperIdentityBootstrap ||
            disposed ||
            bootstrapHelperIdentity is not null ||
            helperIdentityBootstrapPending ||
            bootstrapHelperIdentityResolutionTask is not null ||
            !ShowMainControls)
        {
            return;
        }

        BeginBootstrapHelperIdentityResolution();
        NotifyHelperIdentityBootstrapChanged();
    }

    private async Task ResolveBootstrapHelperIdentityAsync(CancellationToken ct)
    {
        try
        {
            var resolved = await bootstrapHelperIdentityResolver(ct).ConfigureAwait(false);
            if (disposed || ct.IsCancellationRequested)
            {
                return;
            }

            await UiThreadDispatch.RunAsync(() =>
            {
                RefreshAutomaticIdentityRecoveryWarning();
                // Keep the first stable helper identity visible through pre-connected UI states.
                if (bootstrapHelperIdentity is null && resolved is not null)
                {
                    bootstrapHelperIdentity = resolved;
                }

                helperIdentityBootstrapErrorText = string.Empty;
                helperIdentityBootstrapPending = false;
                NotifyHelperIdentityBootstrapChanged();
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (disposed)
            {
                return;
            }

            await UiThreadDispatch.RunAsync(() =>
            {
                RefreshAutomaticIdentityRecoveryWarning();
                helperIdentityBootstrapPending = false;
                NotifyHelperIdentityBootstrapChanged();
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Helper identity bootstrap resolution failed: {ex.Message}");
            if (disposed)
            {
                return;
            }

            await UiThreadDispatch.RunAsync(() =>
            {
                RefreshAutomaticIdentityRecoveryWarning();
                helperIdentityBootstrapErrorText = IsProtectedSeedStorageReadFailure(ex)
                    ? "Protected seed storage could not be read."
                    : "Helper address is unavailable right now.";
                helperIdentityBootstrapPending = false;
                NotifyHelperIdentityBootstrapChanged();
            });
        }
    }

    private void CancelBootstrapHelperIdentityResolution()
    {
        var cts = Interlocked.Exchange(ref bootstrapHelperIdentityResolutionCts, null);
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
            // Best-effort only.
        }
        finally
        {
            cts.Dispose();
        }
    }

    private static bool IsProtectedSeedStorageReadFailure(Exception ex)
    {
        return ex is CryptographicException ||
               ex.Message.Contains("Protected seed storage could not be read.", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("seed storage", StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshAutomaticIdentityRecoveryWarning()
    {
        var warning = PersistenceDiagnostics.Snapshot().LastWarning;
        if (!warning.Contains("created a new local identity", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(automaticIdentityRecoveryWarning, warning, StringComparison.Ordinal))
        {
            return;
        }

        automaticIdentityRecoveryWarning = warning;
    }

    private async Task AwaitBootstrapHelperIdentityResolutionCompletionAsync()
    {
        var pending = bootstrapHelperIdentityResolutionTask;
        if (pending is null)
        {
            return;
        }

        try
        {
            await pending.ConfigureAwait(false);
        }
        catch
        {
            // Resolution failure should not block a real helper connect.
        }
        finally
        {
            if (ReferenceEquals(bootstrapHelperIdentityResolutionTask, pending))
            {
                bootstrapHelperIdentityResolutionTask = null;
            }
        }
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
        var approvalTimedOut = IsApprovalTimeoutFailure();
        connectOutcome?.TrySetResult(approvalTimedOut ? HelperConnectOutcome.Disconnected : HelperConnectOutcome.Rejected);
        _ = UiThreadDispatch.RunAsync(() =>
        {
            IsConnecting = false;
            StatusText = string.IsNullOrWhiteSpace(sessionRuntime.StatusText)
                ? approvalTimedOut ? UserErrorMapper.HelperApprovalTimeout() : UserErrorMapper.HelperRejected()
                : sessionRuntime.StatusText;
            ConnectionState = approvalTimedOut ? "Failed" : "Rejected";
            if (approvalTimedOut)
            {
                LogReliability(SessionReliabilityStage.Disconnected, "approval_timeout", "No response yet.");
            }
            else
            {
                LogReliability(SessionReliabilityStage.Rejected, "rejected", "They did not allow the connection.");
            }
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
            var nknSnapshot = NknRuntimeDiagnostics.Snapshot();
            var remoteSessionEndSeenInDiagnostics =
                string.Equals(nknSnapshot.LastEnvelopeType, "SessionEnd", StringComparison.OrdinalIgnoreCase);
            var runtimeStatus = sessionRuntime.StatusText;
            if (string.IsNullOrWhiteSpace(runtimeStatus))
            {
                // Remote session-end can race with runtime reset back to Idle, clearing StatusText
                // before the helper VM handles the disconnect callback. If we were connected and
                // there is no classified failure, prefer the friendly session-ended message.
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

            var peerEnded =
                sessionRuntime.LastTransportFailure is null &&
                (sessionRuntime.LastDisconnectWasRemoteEnd || remoteSessionEndSeenInDiagnostics);
            if (peerEnded)
            {
                ApplyPeerEndedDisconnectUiState();
            }

            var (errorCode, errorHint) = GetReliabilityError();
            LogReliability(SessionReliabilityStage.Disconnected, errorCode, errorHint);
            if (peerEnded)
            {
                // Remote-end cleanup above already moved the helper UI out of the active session shell state.
            }
            else if (string.Equals(sessionRuntime.StatusText, "Connection lost.", StringComparison.Ordinal))
            {
                ConnectionState = "Failed";
            }
            else if (sessionRuntime.State != SessionRuntimeState.Connected)
            {
                ConnectionState = "Disconnected";
            }
            else if (ConnectionState != "Connected")
            {
                ConnectionState = "Disconnected";
            }
            NotifyDisconnectedUiAffordancesChanged();
        });
    }

    private void OnRemoteSessionEnded(object? sender, EventArgs e)
    {
        if (disposed || connectCts?.IsCancellationRequested == true)
        {
            return;
        }

        _ = UiThreadDispatch.RunAsync(() =>
        {
            ApplyPeerEndedDisconnectUiState();
            NotifyDisconnectedUiAffordancesChanged();
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
            LogCurrentChatPanelState("chat_state_changed");
        });
    }

    private void OnFileTransferChanged(object? sender, EventArgs e)
    {
        if (disposed)
        {
            return;
        }

        _ = UiThreadDispatch.RunAsync(() => UpdateUiFromSnapshot("file_transfer_changed"));
    }

    private void OnRemoteControlStateChanged(object? sender, EventArgs e)
    {
        if (disposed)
        {
            return;
        }

        _ = UiThreadDispatch.RunAsync(() =>
        {
            OnPropertyChanged(nameof(HeaderStatusText));
            OnPropertyChanged(nameof(SessionSupportsRemoteControl));
            OnPropertyChanged(nameof(RemoteControlMappingAvailable));
            OnPropertyChanged(nameof(ShowRequestControlAction));
            OnPropertyChanged(nameof(CanRequestControl));
            OnPropertyChanged(nameof(ShowStopControlAction));
            OnPropertyChanged(nameof(StopControlButtonText));
            OnPropertyChanged(nameof(CanStopControl));
            OnPropertyChanged(nameof(ShowRemoteControlActiveStatus));
            OnPropertyChanged(nameof(IsRemoteControlInputCaptureEnabled));
            OnPropertyChanged(nameof(ShowControlModeToggle));
            OnPropertyChanged(nameof(CanControlModeToggle));
            OnPropertyChanged(nameof(ControlModeButtonText));
            OnPropertyChanged(nameof(IsRemoteControlKeyboardCaptureEnabled));
            OnPropertyChanged(nameof(CanEndSession));
            NotifyRemoteControlDiagnosticsChanged();
            RequestControlCommand.NotifyCanExecuteChanged();
            StopControlCommand.NotifyCanExecuteChanged();
            ToggleControlModeCommand.NotifyCanExecuteChanged();
            EndSessionCommand.NotifyCanExecuteChanged();
            EnsureControlModeConsistency();
            if (sessionRuntime.ControlState != ControlState.Active)
            {
                ResetRemoteControlDebugMetrics();
            }
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

    private void OnSessionSecurityStateChanged(object? sender, EventArgs e)
    {
        if (disposed)
        {
            return;
        }

        _ = UiThreadDispatch.RunAsync(SyncFromRuntime);
    }

    private void OnScreenShareFrameCompleted(object? sender, ScreenShareFrameCompletedEventArgs e)
    {
        ScreenShareViewer.OnJpegFrame(
            e.EncodedFrameBytes,
            e.CapturedTsUtcMs,
            e.ChunksDroppedOlderFrame,
            e.AssembliesExpired);
    }

    private void OnScreenShareStopped(object? sender, EventArgs e)
    {
        if (disposed)
        {
            return;
        }

        LocalOperationalLog.Info(
            "HelperUi",
            $"event=helper_screenshare_viewer_clear_requested; control_state={sessionRuntime.ControlState}; header_status={HeaderStatusText}; has_visible_frame={ShowRemoteScreenShareFrame}");
        _ = UiThreadDispatch.RunAsync(() =>
        {
            ClearRemoteScreenShareFrame();
            LocalOperationalLog.Info(
                "HelperUi",
                $"event=helper_screenshare_viewer_cleared; control_state={sessionRuntime.ControlState}; header_status={HeaderStatusText}; has_visible_frame={ShowRemoteScreenShareFrame}");
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
                OnPropertyChanged(nameof(ShowRequestControlAction));
                OnPropertyChanged(nameof(CanRequestControl));
                OnPropertyChanged(nameof(ShowStopControlAction));
                OnPropertyChanged(nameof(CanStopControl));
                OnPropertyChanged(nameof(IsRemoteControlInputCaptureEnabled));
                OnPropertyChanged(nameof(ShowControlModeToggle));
                OnPropertyChanged(nameof(CanControlModeToggle));
                OnPropertyChanged(nameof(IsRemoteControlKeyboardCaptureEnabled));
                NotifyRemoteControlDiagnosticsChanged();
                RequestControlCommand.NotifyCanExecuteChanged();
                StopControlCommand.NotifyCanExecuteChanged();
                lastKnownShowRemoteScreenShareFrame = nextShowRemoteScreenShareFrame;
                EnsureControlModeConsistency();
                if (!nextShowRemoteScreenShareFrame)
                {
                    ResetRemoteControlDebugMetrics();
                }
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

        _ = UiThreadDispatch.RunAsync(() =>
        {
            SyncTransientStatusFromRuntime();
            OnPropertyChanged(nameof(HeaderStatusText));
        });
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

        return (lastError, "Connection did not complete. Try again with invite, invite code, or address.");
    }

    private void SyncFromRuntime()
    {
        PromoteBootstrapHelperIdentityFromConnectedSessionIfAvailable();
        CacheBootstrapHelperIdentityFromRuntimeIfAvailable();

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
                case SessionRuntimeState.Idle:
                    IsConnecting = false;
                    if (endInvoked)
                    {
                        break;
                    }

                    if (wasConnected && endReason is null)
                    {
                        var inferredReason = sessionRuntime.LastDisconnectWasRemoteEnd || sessionRuntime.LastTransportFailure is null
                            ? SessionEndReason.PeerEnded
                            : SessionEndReason.Failed;
                        endReason = inferredReason;
                        ApplyEndReasonPresentation(inferredReason);
                    }
                    else if (!wasConnected)
                    {
                        ClearFailurePresentation();
                        StatusText = string.IsNullOrWhiteSpace(sessionRuntime.StatusText)
                            ? string.Empty
                            : sessionRuntime.StatusText;
                        ConnectionState = "Idle";
                    }
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
                        EnsureFailurePresentationForTerminalFailure();
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
                        EnsureFailurePresentationForTerminalFailure();
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
        OnPropertyChanged(nameof(HeaderStatusText));
        OnPropertyChanged(nameof(HelperVerificationCode));
        OnPropertyChanged(nameof(HasHelperVerificationCode));
        OnPropertyChanged(nameof(ShowHelperVerificationCode));
        OnPropertyChanged(nameof(HeaderVerificationCodeText));
        OnPropertyChanged(nameof(ShowHeaderVerificationCode));
        OnPropertyChanged(nameof(FirstPillVerificationCodeText));
        OnPropertyChanged(nameof(ShowFirstPillVerificationCode));
        OnPropertyChanged(nameof(HelperTechnicalIdentityText));
        OnPropertyChanged(nameof(HelperTechnicalSessionIdText));
        OnPropertyChanged(nameof(HasHelperTechnicalDetails));
        OnPropertyChanged(nameof(SessionSupportsRemoteControl));
        OnPropertyChanged(nameof(RemoteControlMappingAvailable));
        OnPropertyChanged(nameof(ShowRequestControlAction));
        OnPropertyChanged(nameof(CanRequestControl));
        OnPropertyChanged(nameof(ShowStopControlAction));
        OnPropertyChanged(nameof(StopControlButtonText));
        OnPropertyChanged(nameof(CanStopControl));
        OnPropertyChanged(nameof(ShowRemoteControlActiveStatus));
        OnPropertyChanged(nameof(IsRemoteControlInputCaptureEnabled));
        OnPropertyChanged(nameof(ShowControlModeToggle));
        OnPropertyChanged(nameof(CanControlModeToggle));
        OnPropertyChanged(nameof(ControlModeButtonText));
        OnPropertyChanged(nameof(IsRemoteControlKeyboardCaptureEnabled));
        NotifyRemoteControlDiagnosticsChanged();
        RequestControlCommand.NotifyCanExecuteChanged();
        StopControlCommand.NotifyCanExecuteChanged();
        ToggleControlModeCommand.NotifyCanExecuteChanged();
        ScanQrFromFileCommand.NotifyCanExecuteChanged();
        ScanQrFromCameraCommand.NotifyCanExecuteChanged();
        EnsureControlModeConsistency();
        SendChatCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        OpenDiagnosticsCommand.NotifyCanExecuteChanged();
        EndSessionCommand.NotifyCanExecuteChanged();
        SyncTransientStatusFromRuntime();
        NotifyStatusBannerDetailChanged();
        EnsureBootstrapHelperIdentityResolutionForReadyState();
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

    private void EnsureFailurePresentationForTerminalFailure()
    {
        if (IsApprovalTimeoutFailure())
        {
            EnsureFailurePresentation(
                "No response yet",
                "The other person did not respond in time.",
                "Retry");
            return;
        }

        EnsureFailurePresentation(
            "Connection failed",
            "The session ended due to a connection problem.",
            "Retry");
    }

    private bool IsApprovalTimeoutFailure()
    {
        return string.Equals(sessionRuntime.StatusText, UserErrorMapper.HelperApprovalTimeout(), StringComparison.Ordinal) ||
               sessionRuntime.LastTransportFailure?.Category == TransportFailureCategory.HandshakeTimeout;
    }

    private void NotifyRemoteControlDiagnosticsChanged()
    {
        OnPropertyChanged(nameof(ShowRemoteControlDebugToggle));
        OnPropertyChanged(nameof(ShowRemoteControlDebugPanel));
        OnPropertyChanged(nameof(RemoteControlDebugToggleText));
        OnPropertyChanged(nameof(RemoteControlDiagnosticsRoleText));
        OnPropertyChanged(nameof(RemoteControlDiagnosticsControlStateText));
        OnPropertyChanged(nameof(RemoteControlDiagnosticsControlModeText));
        OnPropertyChanged(nameof(RemoteControlDiagnosticsDisplayText));
        OnPropertyChanged(nameof(RemoteControlDiagnosticsCaptureFrameText));
        OnPropertyChanged(nameof(RemoteControlDiagnosticsMoveStatsText));
        OnPropertyChanged(nameof(RemoteControlDiagnosticsSuppressionsText));
        OnPropertyChanged(nameof(RemoteControlDiagnosticsLastMappedText));
        OnPropertyChanged(nameof(RemoteControlDebugControlStateText));
        OnPropertyChanged(nameof(RemoteControlDebugDisplayRevisionText));
        OnPropertyChanged(nameof(RemoteControlDebugRequestIdText));
        OnPropertyChanged(nameof(RemoteControlDebugControllerPeerText));
        OnPropertyChanged(nameof(RemoteControlDebugMappingDisplayText));
        OnPropertyChanged(nameof(RemoteControlDebugControlModeText));
        OnPropertyChanged(nameof(RemoteControlDebugMouseMoveRateText));
        OnPropertyChanged(nameof(RemoteControlDebugGuardrailCountersText));
        ToggleRemoteControlDebugPanelCommand.NotifyCanExecuteChanged();
    }

    private RemoteControlDebugSnapshot GetRemoteControlDiagnosticsSnapshot()
    {
#if DEBUG
        return RemoteControlDebugDiagnostics.Snapshot(RemoteControlDiagnosticsRole.Helper);
#else
        return RemoteControlDebugSnapshot.Empty(RemoteControlDiagnosticsRole.Helper);
#endif
    }

    private static string FormatCaptureFrameText(RemoteControlDebugSnapshot snapshot)
    {
        var capture = snapshot.CaptureRegionPx.HasValue
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{snapshot.CaptureRegionPx.Value.X},{snapshot.CaptureRegionPx.Value.Y},{snapshot.CaptureRegionPx.Value.Width}x{snapshot.CaptureRegionPx.Value.Height}")
            : "n/a";
        var frame = snapshot.FrameSizePx.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $"{snapshot.FrameSizePx.Value.Width}x{snapshot.FrameSizePx.Value.Height}")
            : "n/a";
        return $"capture={capture}; frame={frame}";
    }

    private string FormatMoveStatsText(RemoteControlDebugSnapshot snapshot)
    {
        var sentPerSecond = snapshot.MouseMoveSentPerSec ?? remoteControlDebugMouseMovesPerSecond;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"sent={sentPerSecond}/s; dropped={snapshot.MouseMoveDropped}; clamps={snapshot.OutOfRangeClamps}");
    }

    private static string FormatSuppressionText(RemoteControlDebugSnapshot snapshot)
    {
        var ackAgeMs = snapshot.HelperLastAckAgeMs?.ToString(CultureInfo.InvariantCulture) ?? "n/a";
        var snapshotRate = snapshot.HelperSnapshotSentPerSec?.ToString(CultureInfo.InvariantCulture) ?? "n/a";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"suppressed={snapshot.SuppressedInjections}; flushes={snapshot.QueueFlushes}; ack_seq={snapshot.HelperLastAckSeq}; ack_age_ms={ackAgeMs}; ack_stalls={snapshot.HelperStallDetectedCount}; stall_recovery_sent={snapshot.HelperStallRecoverySentCount}; snap_tx_seq={snapshot.HelperLastSnapshotSentSeq}; snap_tx_masks={snapshot.HelperLastSnapshotSentModifiersMask}/{snapshot.HelperLastSnapshotSentMouseButtonsMask}; snap_tx_rate={snapshotRate}/s");
    }

    private static string FormatLastMappedText(RemoteControlDebugSnapshot snapshot)
    {
        if (!snapshot.LastMapped.HasValue)
        {
            return "n/a";
        }

        var mapped = snapshot.LastMapped.Value;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"nx={mapped.Nx:0.###}, ny={mapped.Ny:0.###} -> px={mapped.Px}, py={mapped.Py}");
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
        UpdateUiFromSnapshot("refresh");
    }

    private void UpdateUiFromSnapshot(string source)
    {
        bool nextChatEnabled;
        bool nextCanSendFiles;
        bool nextCanEndSession;
        bool nextCanOpenDiagnostics;
        var fileTransferSnapshot = sessionRuntime.FileTransferSnapshot;
        InboundFileTransfer = FileTransferPanelItemViewModel.FromSnapshot(fileTransferSnapshot.Inbound);
        OutboundFileTransfer = FileTransferPanelItemViewModel.FromSnapshot(fileTransferSnapshot.Outbound);
        var hasActiveOutboundTransfer = fileTransferSnapshot.Outbound is { IsTerminal: false };
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
            nextCanSendFiles = phase == SessionUiPhase.Connected &&
                               sessionRuntime.CanPerform(SessionCapability.FileTransfer) &&
                               !hasActiveOutboundTransfer;
            nextChatEnabled = phase == SessionUiPhase.Connected;
        }
        else
        {
            switch (phase)
            {
                case SessionUiPhase.Connected:
                    nextChatEnabled = true;
                    nextCanSendFiles = sessionRuntime.CanPerform(SessionCapability.FileTransfer) &&
                                       !hasActiveOutboundTransfer;
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
        OnPropertyChanged(nameof(ShowSendFileAction));
        OnPropertyChanged(nameof(CanSendFileAction));
        SendChatCommand.NotifyCanExecuteChanged();
        SendFileCommand.NotifyCanExecuteChanged();
        AcceptIncomingFileCommand.NotifyCanExecuteChanged();
        DeclineIncomingFileCommand.NotifyCanExecuteChanged();
        CancelFileTransferCommand.NotifyCanExecuteChanged();
        OpenDiagnosticsCommand.NotifyCanExecuteChanged();
        EndSessionCommand.NotifyCanExecuteChanged();
        LogCurrentChatPanelState(source);
        AssertUiConsistency();
    }

    private void LogCurrentChatPanelState(string source)
    {
        var fileTransferSnapshot = sessionRuntime.FileTransferSnapshot;
        var payload =
            $"event=helper_chat_panel_state; source={source}; phase={EffectivePhase}; connection_state={ConnectionState}; " +
            $"runtime_state={sessionRuntime.State}; runtime_can_send_chat={sessionRuntime.CanSendChat}; " +
            $"chat_input_enabled={IsChatInputEnabled}; send_command_enabled={SendChatCommand.CanExecute(null)}; " +
            $"draft_len={ChatDraft.Length}; can_send_files={CanSendFiles}; " +
            $"outbound_state={fileTransferSnapshot.Outbound?.State.ToString() ?? "(none)"}; " +
            $"outbound_terminal={fileTransferSnapshot.Outbound?.IsTerminal.ToString() ?? "(none)"}; " +
            $"inbound_state={fileTransferSnapshot.Inbound?.State.ToString() ?? "(none)"}; " +
            $"inbound_terminal={fileTransferSnapshot.Inbound?.IsTerminal.ToString() ?? "(none)"}";
        if (string.Equals(payload, lastChatPanelStateLog, StringComparison.Ordinal))
        {
            return;
        }

        lastChatPanelStateLog = payload;
        LocalOperationalLog.Info("HelperUi", payload);
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

    private bool TryGetRemoteControlHeaderHint(out string hintText)
    {
        hintText = string.Empty;
        if (!IsConnectedView || !sessionRuntime.IsTransientStatusVisible)
        {
            return false;
        }

        var transientText = sessionRuntime.TransientStatusText;
        if (string.IsNullOrWhiteSpace(transientText) ||
            !transientText.StartsWith("Screen changed", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        hintText = SanitizeTransientText(transientText);
        return !string.IsNullOrWhiteSpace(hintText);
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

        if (!endInvoked &&
            HeaderStatusText.StartsWith("Connected", StringComparison.Ordinal) &&
            sessionRuntime.State == SessionRuntimeState.Connected &&
            (IsConnectedView || uiStateStore?.Phase == SessionUiPhase.Connected) &&
            !IsChatInputEnabled)
        {
            throw new InvalidOperationException("UI invariant failed: Connected header requires chat enabled.");
        }

        if (string.IsNullOrWhiteSpace(HeaderStatusText))
        {
            throw new InvalidOperationException("Helper UI invariant failed: header status text must not be empty.");
        }
    }

    private void PrepareForNewSession(bool clearConnectInput = true)
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
        if (clearConnectInput)
        {
            CodeInput = string.Empty;
        }
        ChatDraft = string.Empty;
        ChatMessages.Clear();
        OnPropertyChanged(nameof(HasChatMessages));
        OnPropertyChanged(nameof(ShowNoMessagesPlaceholder));
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
                CodeInput = string.Empty;
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

    private void ApplyPeerEndedDisconnectUiState()
    {
        endReason = SessionEndReason.PeerEnded;
        ApplyEndReasonPresentation(SessionEndReason.PeerEnded);
        uiStateStore?.SetPhase(SessionUiPhase.Ended, "OnDisconnected:PeerEnded");
        fallbackUiPhase = SessionUiPhase.Ended;
        ApplySessionBannerPolicy();
        UpdateUiFromSnapshot();
    }

    private void NotifyDisconnectedUiAffordancesChanged()
    {
        OnPropertyChanged(nameof(ShowChatConnectionHint));
        OnPropertyChanged(nameof(ShowMainControls));
        OnPropertyChanged(nameof(ShowConnectAction));
        OnPropertyChanged(nameof(ShowRetryAction));
        OnPropertyChanged(nameof(ShowInlineStatusText));
        OnPropertyChanged(nameof(ShowCopyFeedbackInline));
        OnPropertyChanged(nameof(HeaderStatusText));
        ScanQrFromFileCommand.NotifyCanExecuteChanged();
        ScanQrFromCameraCommand.NotifyCanExecuteChanged();
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
        if (!ScreenShareViewer.IsActive ||
            (RemoteScreenShareFrame is null && string.IsNullOrWhiteSpace(ScreenShareViewerMessage)))
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
}
