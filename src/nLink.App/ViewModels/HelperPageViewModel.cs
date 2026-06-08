using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
    private static readonly TimeSpan DefaultApprovalTimeout = SessionApprovalTimeouts.DefaultHumanDecisionTimeout;
    private static readonly TimeSpan DisposeOperationTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RecoveryTransientThrottle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan EndSessionAfterControlStopGuard = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RemoteControlSnapshotKeepAliveInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PeerEndedNoticeDuration = TimeSpan.FromSeconds(4);
    private const int FileTransferUiRefreshMinIntervalMs = 125;
    private const int FileTransferUiRefreshCoalescedLogThreshold = 8;
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
    private readonly IQrCodeService qrCodeService;
    private readonly ShareMessageConfig shareMessageConfig;
    private readonly SessionUiStateStore? uiStateStore;
    private readonly IConnectInputResolver connectInputResolver;
    private readonly DispatcherTimer remoteControlStateSnapshotTimer;
    private readonly DispatcherTimer peerEndedNoticeTimer;
    private readonly DispatcherTimer incomingHelpRequestExpiryTimer;
    private readonly Func<CancellationToken, Task<PeerAddress?>>? bootstrapHelperIdentityResolver;
    private string automaticIdentityRecoveryWarning = string.Empty;
    private CancellationTokenSource? bootstrapHelperIdentityResolutionCts;
    private PeerAddress? bootstrapHelperIdentity;
    private bool bootstrapHelperIdentityIsAuthoritative;
    private PeerAddress? previewInviteBoundHelperIdentity;
    private bool helperIdentityBootstrapPending;
    private string helperIdentityBootstrapErrorText = string.Empty;
    private Bitmap? helperBootstrapQrBitmap;
    private string lastChatPanelStateLog = string.Empty;
    private long chatSendAttemptCounter;
    private int fileTransferUiRefreshScheduled;
    private int fileTransferUiRefreshPendingCount;
    private int fileTransferUiRefreshUrgent;
    private long lastFileTransferUiRefreshUtcMs;

    private string codeInput = string.Empty;
    private string statusText = string.Empty;
    private string connectionState = "Idle";
    private bool isTunaActive;
    private string tunaStatusReason = "inactive";
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
    private SessionFileTransferSnapshot? pendingFileTransferUiSnapshot;
    private bool isChatInputEnabled;
    private bool controlModeEnabled;
    private SessionUiPhase effectivePhase;
    private bool localEndCommandInFlight;
    private DateTimeOffset endSessionGuardUntilUtc = DateTimeOffset.MinValue;
    private CancellationTokenSource? connectCts;
    private Task? bootstrapHelperIdentityResolutionTask;
    private string lastPublishedHelperBootstrapSnapshotKey = string.Empty;
    private readonly InlineTransientText copyFeedback = new();
    private readonly Func<DateTimeOffset> nowProvider;
    private readonly TimeSpan connectFailureCooldown;
    private readonly TimeSpan approvalTimeout;
    private DateTimeOffset incomingHelpRequestExpiresAtUtc = DateTimeOffset.MinValue;
    private string incomingHelpRequestTimeoutText = string.Empty;
    private CancellationTokenSource? incomingHelpRequestTimeoutCts;
    private readonly Func<CancellationToken, Task<PeerAddress?>>? regenerateHelperIdentityAsync;
    private DateTimeOffset lastFailedAttemptUtc = DateTimeOffset.MinValue;
    private TaskCompletionSource<HelperConnectOutcome>? connectOutcome;
    private SessionReliabilityAttempt? reliabilityAttempt;
    private SessionUiPhase lastObservedUiPhase;
    private bool lastKnownShowRemoteScreenShareFrame;
    private bool lastKnownShowHelperMainContent = true;
    private string lastKnownHeaderStatusText = "Ready";
    private string lastKnownScreenShareViewerMessage = string.Empty;
    private bool lastKnownShowScreenShareViewerError;
    private bool helperRemoteSurfaceVisibleLogged;
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
    private string lastRemoteControlInputUiLog = string.Empty;
    private bool disposed;
    private int windowCloseDisconnectStarted;
    private bool showPeerEndedNotice;
    private string peerEndedNoticeText = string.Empty;
    private string lastPeerEndedNoticeKey = string.Empty;
    private string lastConversationSessionId = string.Empty;
    private bool suppressRetryActionForReturnToWaiting;
    private bool helperListenerReturnToWaitingRequested;
    private string lastHelperBootstrapQrPayload = string.Empty;
    private bool helperIdentityRegenerationInFlight;

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
        Func<CancellationToken, Task<PeerAddress?>>? regenerateHelperIdentityAsync = null,
        IInviteShareService? inviteShareService = null,
        IQrCodeService? qrCodeService = null)
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
        this.qrCodeService = qrCodeService ?? new QrCodeService();
        this.shareMessageConfig = shareMessageConfig ?? new ShareMessageConfig(null);
        this.uiStateStore = uiStateStore;
        this.connectInputResolver = connectInputResolver ?? ConnectInputResolverFactory.CreateDefault();
        this.bootstrapHelperIdentityResolver = bootstrapHelperIdentityResolver;
        this.regenerateHelperIdentityAsync = regenerateHelperIdentityAsync ??
            (string.Equals(transportConfig.Key, "NKN", StringComparison.OrdinalIgnoreCase)
                ? NknLocalPeerAddressResolver.RegeneratePersistedIdentityAsync
                : null);
        RefreshAutomaticIdentityRecoveryWarning();
        this.approvalTimeout = approvalTimeout ?? DefaultApprovalTimeout;
        this.connectFailureCooldown = connectFailureCooldown ?? DefaultConnectFailureCooldown;
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
        lastObservedUiPhase = uiStateStore?.Phase ?? SessionUiPhase.Idle;
        ScreenShareViewer = new ScreenShareViewerViewModel(
            decodeFrame: null,
            postToUiAsync: null,
            h264Decoder: null,
            logRole: "helper_remote");
        remoteControlStateSnapshotTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(FeatureFlags.RemoteControlStateSnapshotIntervalMs),
        };
        remoteControlStateSnapshotTimer.Tick += OnRemoteControlStateSnapshotTimerTick;
        peerEndedNoticeTimer = new DispatcherTimer
        {
            Interval = PeerEndedNoticeDuration,
        };
        peerEndedNoticeTimer.Tick += OnPeerEndedNoticeTimerTick;
        incomingHelpRequestExpiryTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        incomingHelpRequestExpiryTimer.Tick += OnIncomingHelpRequestExpiryTimerTick;
        lastKnownShowRemoteScreenShareFrame = ShowRemoteScreenShareFrame;
        lastKnownShowHelperMainContent = ShowHelperMainContent;
        lastKnownScreenShareViewerMessage = ScreenShareViewerMessage;
        lastKnownShowScreenShareViewerError = ShowScreenShareViewerError;
        lastKnownHeaderStatusText = HeaderStatusText;

        ChatMessages = new ObservableCollection<ChatLineViewModel>();

        sessionRuntime.FlowSnapshotChanged += OnFlowSnapshotChanged;
        sessionRuntime.SessionSecurityStateChanged += OnSessionSecurityStateChanged;
        sessionRuntime.TransportAccelerationStateChanged += OnTransportAccelerationStateChanged;
        sessionRuntime.TransientStatusChanged += OnTransientStatusChanged;
        sessionRuntime.Approved += OnApproved;
        sessionRuntime.Rejected += OnRejected;
        sessionRuntime.Disconnected += OnDisconnected;
        sessionRuntime.RemoteSessionEnded += OnRemoteSessionEnded;
        sessionRuntime.ScreenShareFrameCompleted += OnScreenShareFrameCompleted;
        sessionRuntime.ScreenShareStopped += OnScreenShareStopped;
        sessionRuntime.ScreenShareCursorStateReceived += OnScreenShareCursorStateReceived;
        sessionRuntime.ChatMessageReceived += OnChatMessageReceived;
        sessionRuntime.ChatMessageReceivedBeforeApproved += OnChatMessageReceivedBeforeApproved;
        sessionRuntime.ChatStateChanged += OnChatStateChanged;
        sessionRuntime.FileTransferChanged += OnFileTransferChanged;
        sessionRuntime.RemoteControlStateChanged += OnRemoteControlStateChanged;
        sessionRuntime.IncomingHelpRequestAvailable += OnIncomingHelpRequestAvailable;
        sessionRuntime.HelperListenerBootstrapSnapshotChanged += OnHelperListenerBootstrapSnapshotChanged;
        this.statusPresenter.StatusChanged += OnStatusPresenterChanged;
        copyFeedback.PropertyChanged += OnCopyFeedbackPropertyChanged;
        ScreenShareViewer.PropertyChanged += OnScreenShareViewerPropertyChanged;
        ScreenShareViewer.FrameApplied += OnScreenShareViewerFrameApplied;
        ScreenShareViewer.StaleFrameDropped += OnScreenShareViewerStaleFrameDropped;
        ScreenShareViewer.DecodeNeedsMoreInput += OnScreenShareViewerDecodeNeedsMoreInput;
        ScreenShareViewer.ContinuityLost += OnScreenShareViewerContinuityLost;
        ScreenShareViewer.RecoveryKeyframeApplied += OnScreenShareViewerRecoveryKeyframeApplied;
        ScreenShareViewer.RecoveryWindowStateChanged += OnScreenShareViewerRecoveryWindowStateChanged;
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
        PauseFileTransferCommand = new AsyncRelayCommand<string?>(PauseFileTransferAsync, CanPauseFileTransfer);
        ResumeFileTransferCommand = new AsyncRelayCommand<string?>(ResumeFileTransferAsync, CanResumeFileTransfer);
        RetryCommand = new AsyncRelayCommand(RetryAsync, CanRetry);
        CancelTransientCommand = new AsyncRelayCommand(CancelTransientAsync, CanCancelTransientOperation);
        OpenDiagnosticsCommand = new RelayCommand(OpenDiagnostics, CanOpenDiagnosticsCommand);
        CancelCommand = new RelayCommand(CancelAndGoBack);
        EndSessionCommand = new RelayCommand(EndSession, CanTriggerEndSession);
        ScanQrFromFileCommand = new RelayCommand(RequestScanQrFromFile, () => ShowMainControls);
        ScanQrFromCameraCommand = new RelayCommand(RequestScanQrFromCamera, () => ShowMainControls);
        AcceptHelpRequestCommand = new AsyncRelayCommand(AcceptHelpRequestAsync, CanRespondToHelpRequest);
        RejectHelpRequestCommand = new AsyncRelayCommand(RejectHelpRequestAsync, CanRespondToHelpRequest);
        RegenerateHelperIdentityCommand = new AsyncRelayCommand(RegenerateHelperIdentityAsync, () => CanRegenerateHelperIdentity);
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
        SyncTunaActiveFromRuntime();
        BeginBootstrapHelperIdentityResolution();
        if (!IsStartupBlocked)
        {
            _ = StartListeningAsync();
        }
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
                NotifySessionVerificationPropertiesChanged();
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
                NotifyRegenerateHelperIdentityStateChanged();
            }
        }
    }

    public string HelperPageHelpText => "Paste invite or invite code.";

    public string ConnectionMethodHint => transportConfig.HelperHintText;

    private PeerAddress? HelperVerificationIdentity => sessionRuntime.SecurityState.HelperAddress;
    private PeerAddress? HelperRequestTargetAddress => sessionRuntime.CurrentLocalPeerAddress;
    private bool HasAuthoritativeHelperRequestTargetAddress =>
        HelperRequestTargetAddress is not null &&
        EffectivePhase == SessionUiPhase.Waiting &&
        sessionRuntime.Role == SessionRuntimeRole.Helper &&
        sessionRuntime.State == SessionRuntimeState.Waiting;
    private PeerAddress? HelperIdentityForInviteBinding => bootstrapHelperIdentity ?? HelperRequestTargetAddress;
    private PeerAddress? HelperCanonicalBootstrapVerificationIdentity =>
        bootstrapHelperIdentityIsAuthoritative
            ? HelperIdentityForInviteBinding
            : HasAuthoritativeHelperRequestTargetAddress
                ? HelperRequestTargetAddress
            : null;
    private PeerAddress? HelperIdentityForDisplay =>
        HelperRequestTargetAddress ??
        bootstrapHelperIdentity;
    private bool IsNknTransport =>
        string.Equals(transportConfig.Key, "NKN", StringComparison.OrdinalIgnoreCase);
    private bool RequiresHelperIdentityBootstrap =>
        IsNknTransport &&
        InviteSecurityDiagnostics.RequiresBoundHelperForIssuedSecretInvites();

    public string HelperIdentityBootstrapText => BuildHelperBootstrapDisplayValue();

    public string HelperIdentityBootstrapWatermarkText =>
        string.IsNullOrWhiteSpace(HelperIdentityBootstrapText)
            ? "Please wait..."
            : "Helper address unavailable";

    public string HelperIdentityBootstrapHintText =>
        (helperIdentityBootstrapPending ||
         (string.IsNullOrWhiteSpace(HelperIdentityBootstrapText) &&
          string.Equals(helperIdentityBootstrapErrorText, "Helper address is unavailable right now.", StringComparison.Ordinal)))
            ? "Please wait..."
            : !string.IsNullOrWhiteSpace(helperIdentityBootstrapErrorText)
            ? !string.IsNullOrWhiteSpace(automaticIdentityRecoveryWarning) &&
              string.Equals(helperIdentityBootstrapErrorText, "Helper address is unavailable right now.", StringComparison.Ordinal)
                ? $"{automaticIdentityRecoveryWarning} Preparing your helper address."
                : helperIdentityBootstrapErrorText
            : !string.IsNullOrWhiteSpace(automaticIdentityRecoveryWarning)
            ? string.IsNullOrWhiteSpace(HelperIdentityBootstrapText)
                ? $"{automaticIdentityRecoveryWarning} Preparing your helper address."
                : $"{automaticIdentityRecoveryWarning} The helpee enters this helper address to request help."
            : string.IsNullOrWhiteSpace(HelperIdentityBootstrapText)
            ? "Preparing your helper address."
            : "The helpee enters this helper address to request help.";

    public string HelperIdentityBootstrapVerificationCode =>
        HelperVerificationCodeFormatter.FormatOrNull(HelperCanonicalBootstrapVerificationIdentity) ?? string.Empty;

    public bool HasHelperIdentityBootstrapVerificationCode =>
        !string.IsNullOrWhiteSpace(HelperIdentityBootstrapVerificationCode);

    public bool HasReadyHelperIdentityBootstrapText =>
        !string.IsNullOrWhiteSpace(HelperIdentityBootstrapText);

    public bool ShowHelperSetupPanel =>
        ShowMainControls &&
        !HasPendingHelpRequest;

    public bool ShowHelperIdentityBootstrapPanel =>
        ShowHelperSetupPanel &&
        RequiresHelperIdentityBootstrap;

    public bool ShowRegenerateHelperIdentityAction =>
        IsNknTransport &&
        ShowMainControls;

    public bool CanRegenerateHelperIdentity =>
        ShowRegenerateHelperIdentityAction &&
        !helperIdentityRegenerationInFlight &&
        !IsConnecting &&
        !HasPendingHelpRequest &&
        sessionRuntime.ControlState == ControlState.Off &&
        sessionRuntime.State is SessionRuntimeState.Idle or SessionRuntimeState.Waiting;

    public string RegenerateHelperIdentityButtonText =>
        helperIdentityRegenerationInFlight
            ? "Regenerating..."
            : "Regenerate helper address";

    public Bitmap? HelperBootstrapQrImage => helperBootstrapQrBitmap;
    public bool ShowHelperBootstrapQr => HelperBootstrapQrImage is not null;
    public bool ShowHelperBootstrapQrPlaceholder => !ShowHelperBootstrapQr;
    public bool HasPendingHelpRequest => sessionRuntime.HasPendingHelpRequest;
    public string IncomingHelpRequestText =>
        sessionRuntime.PendingHelpRequest is { } request
            ? $"Incoming request from {request.HelpeeAddress.Value}"
            : string.Empty;

    public string IncomingHelpRequestTimeoutText => incomingHelpRequestTimeoutText;

    public bool ShowIncomingHelpRequestTimeout =>
        HasPendingHelpRequest &&
        !string.IsNullOrWhiteSpace(IncomingHelpRequestTimeoutText);

    private SessionVerificationCode? CurrentSessionVerificationCode =>
        sessionRuntime.FlowSnapshot.VerificationCode ?? sessionRuntime.SecurityState.VerificationCode;

    public string SessionVerificationEmojiSequence =>
        CurrentSessionVerificationCode?.EmojiSequence ?? string.Empty;

    public string SessionVerificationFallbackCode =>
        CurrentSessionVerificationCode?.FallbackCode ?? string.Empty;

    public bool HasSessionVerificationCode =>
        !string.IsNullOrWhiteSpace(SessionVerificationEmojiSequence) &&
        !string.IsNullOrWhiteSpace(SessionVerificationFallbackCode);

    public bool ShowSessionVerificationCode =>
        HasSessionVerificationCode &&
        !sessionRuntime.SecurityState.ApprovalGranted &&
        sessionRuntime.SecurityState.HandshakeState == SessionHandshakeState.Verified &&
        EffectivePhase is SessionUiPhase.Connecting or SessionUiPhase.Recovering;

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
        HasReadyHelperIdentityBootstrapText &&
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
        SessionFlowViewProjection.IsConnectedShell(sessionRuntime.FlowSnapshot);

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
                NotifyRegenerateHelperIdentityStateChanged();
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
            : showPeerEndedNotice && !string.IsNullOrWhiteSpace(peerEndedNoticeText)
                ? peerEndedNoticeText
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

    public bool IsTunaActive
    {
        get => isTunaActive;
        private set => SetProperty(ref isTunaActive, value);
    }

    public string TunaStatusReason
    {
        get => tunaStatusReason;
        private set => SetProperty(ref tunaStatusReason, string.IsNullOrWhiteSpace(value) ? "inactive" : value.Trim());
    }

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
                NotifySessionVerificationPropertiesChanged();
                NotifyRegenerateHelperIdentityStateChanged();
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

    public IAsyncRelayCommand<string?> PauseFileTransferCommand { get; }

    public IAsyncRelayCommand<string?> ResumeFileTransferCommand { get; }

    public IAsyncRelayCommand RetryCommand { get; }
    public IAsyncRelayCommand CancelTransientCommand { get; }

    public IRelayCommand OpenDiagnosticsCommand { get; }

    public IRelayCommand CancelCommand { get; }
    public IRelayCommand EndSessionCommand { get; }
    public IRelayCommand ScanQrFromFileCommand { get; }
    public IRelayCommand ScanQrFromCameraCommand { get; }
    public IAsyncRelayCommand AcceptHelpRequestCommand { get; }
    public IAsyncRelayCommand RejectHelpRequestCommand { get; }
    public IAsyncRelayCommand RegenerateHelperIdentityCommand { get; }
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
                                   !suppressRetryActionForReturnToWaiting &&
                                   !sessionRuntime.IsHelperListenerRestartInProgress &&
                                   sessionRuntime.FlowSnapshot.ShowRetryAction;

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
        peerEndedNoticeTimer.Stop();
        peerEndedNoticeTimer.Tick -= OnPeerEndedNoticeTimerTick;
        incomingHelpRequestExpiryTimer.Stop();
        incomingHelpRequestExpiryTimer.Tick -= OnIncomingHelpRequestExpiryTimerTick;
        CancelIncomingHelpRequestTimeout();

        sessionRuntime.FlowSnapshotChanged -= OnFlowSnapshotChanged;
        sessionRuntime.SessionSecurityStateChanged -= OnSessionSecurityStateChanged;
        sessionRuntime.TransportAccelerationStateChanged -= OnTransportAccelerationStateChanged;
        sessionRuntime.TransientStatusChanged -= OnTransientStatusChanged;
        sessionRuntime.Approved -= OnApproved;
        sessionRuntime.Rejected -= OnRejected;
        sessionRuntime.Disconnected -= OnDisconnected;
        sessionRuntime.RemoteSessionEnded -= OnRemoteSessionEnded;
        sessionRuntime.ScreenShareFrameCompleted -= OnScreenShareFrameCompleted;
        sessionRuntime.ScreenShareStopped -= OnScreenShareStopped;
        sessionRuntime.ScreenShareCursorStateReceived -= OnScreenShareCursorStateReceived;
        sessionRuntime.ChatMessageReceived -= OnChatMessageReceived;
        sessionRuntime.ChatMessageReceivedBeforeApproved -= OnChatMessageReceivedBeforeApproved;
        sessionRuntime.ChatStateChanged -= OnChatStateChanged;
        sessionRuntime.FileTransferChanged -= OnFileTransferChanged;
        sessionRuntime.RemoteControlStateChanged -= OnRemoteControlStateChanged;
        sessionRuntime.IncomingHelpRequestAvailable -= OnIncomingHelpRequestAvailable;
        sessionRuntime.HelperListenerBootstrapSnapshotChanged -= OnHelperListenerBootstrapSnapshotChanged;
        statusPresenter.StatusChanged -= OnStatusPresenterChanged;
        copyFeedback.PropertyChanged -= OnCopyFeedbackPropertyChanged;
        ScreenShareViewer.PropertyChanged -= OnScreenShareViewerPropertyChanged;
        ScreenShareViewer.FrameApplied -= OnScreenShareViewerFrameApplied;
        ScreenShareViewer.StaleFrameDropped -= OnScreenShareViewerStaleFrameDropped;
        ScreenShareViewer.DecodeNeedsMoreInput -= OnScreenShareViewerDecodeNeedsMoreInput;
        ScreenShareViewer.ContinuityLost -= OnScreenShareViewerContinuityLost;
        ScreenShareViewer.RecoveryKeyframeApplied -= OnScreenShareViewerRecoveryKeyframeApplied;
        ScreenShareViewer.RecoveryWindowStateChanged -= OnScreenShareViewerRecoveryWindowStateChanged;
        if (uiStateStore is not null)
        {
            uiStateStore.PropertyChanged -= OnUiStateStorePropertyChanged;
        }
        if (ownsStatusPresenter)
        {
            statusPresenter.Dispose();
        }
        sessionRuntime.SetReliabilityAttempt(null);
        var windowCloseAlreadyRequested =
            Interlocked.CompareExchange(ref windowCloseDisconnectStarted, 0, 0) != 0;
        var skipDisconnectForListenerRecovery =
            helperListenerReturnToWaitingRequested &&
            !windowCloseAlreadyRequested;
        if (!skipDisconnectForListenerRecovery && !windowCloseAlreadyRequested)
        {
            RunBoundedSynchronousCleanup(() => sessionRuntime.DisconnectAsync(), DisposeOperationTimeout);
        }

        connectCts?.Cancel();
        connectCts?.Dispose();
        connectCts = null;
        helperBootstrapQrBitmap?.Dispose();
        helperBootstrapQrBitmap = null;
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

    private bool CanRespondToHelpRequest()
    {
        return sessionRuntime.HasPendingHelpRequest && !IsStartupBlocked;
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
        NotifySessionVerificationPropertiesChanged();
        NotifyRegenerateHelperIdentityStateChanged();
        RecordHelperBootstrapDiagnostics();
    }

    private void NotifySessionVerificationPropertiesChanged()
    {
        OnPropertyChanged(nameof(SessionVerificationEmojiSequence));
        OnPropertyChanged(nameof(SessionVerificationFallbackCode));
        OnPropertyChanged(nameof(HasSessionVerificationCode));
        OnPropertyChanged(nameof(ShowSessionVerificationCode));
    }

    private void NotifyRegenerateHelperIdentityStateChanged()
    {
        OnPropertyChanged(nameof(ShowRegenerateHelperIdentityAction));
        OnPropertyChanged(nameof(CanRegenerateHelperIdentity));
        OnPropertyChanged(nameof(RegenerateHelperIdentityButtonText));
        RegenerateHelperIdentityCommand.NotifyCanExecuteChanged();
    }

    private void RecordHelperBootstrapDiagnostics()
    {
        if (!RequiresHelperIdentityBootstrap)
        {
            return;
        }

        NknRuntimeDiagnostics.SetHelperBootstrapDiagnostics(
            ResolveHelperBootstrapAddressSource(),
            HelperCanonicalBootstrapVerificationIdentity is not null,
            ShowFirstPillVerificationCode || ShowHeaderVerificationCode);
    }

    private string ResolveHelperBootstrapAddressSource()
    {
        if (sessionRuntime.State == SessionRuntimeState.Connected &&
            sessionRuntime.SecurityState.HelperAddress is not null)
        {
            return "connected_session";
        }

        if (HasAuthoritativeHelperRequestTargetAddress)
        {
            return "listener";
        }

        if (bootstrapHelperIdentity is not null)
        {
            return bootstrapHelperIdentityIsAuthoritative ? "listener_snapshot" : "persisted";
        }

        return "(none)";
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

    private void OnIncomingHelpRequestAvailable(object? sender, EventArgs e)
    {
        _ = UiThreadDispatch.RunAsync(ApplyIncomingHelpRequestAvailable);
    }

    private void ApplyIncomingHelpRequestAvailable()
    {
        localEndCommandInFlight = false;
        IsConnecting = false;
        if (HasPendingHelpRequest)
        {
            StartIncomingHelpRequestTimeout();
        }
        else
        {
            CancelIncomingHelpRequestTimeout();
        }

        NotifyHelpRequestPresentationChanged();
        NotifyRegenerateHelperIdentityStateChanged();
        RefreshCommandStates();
    }

    private void NotifyHelpRequestPresentationChanged()
    {
        OnPropertyChanged(nameof(HasPendingHelpRequest));
        OnPropertyChanged(nameof(IncomingHelpRequestText));
        OnPropertyChanged(nameof(ShowIncomingHelpRequestTimeout));
        OnPropertyChanged(nameof(ShowHelperSetupPanel));
        OnPropertyChanged(nameof(ShowHelperIdentityBootstrapPanel));
        OnPropertyChanged(nameof(HeaderVerificationCodeText));
        OnPropertyChanged(nameof(ShowHeaderVerificationCode));
        OnPropertyChanged(nameof(ShowFirstPillVerificationCode));
    }

    private void StartIncomingHelpRequestTimeout()
    {
        CancelIncomingHelpRequestTimeout();

        incomingHelpRequestExpiresAtUtc = nowProvider() + approvalTimeout;
        UpdateIncomingHelpRequestTimeoutText(BuildIncomingHelpRequestTimeoutText(nowProvider()));
        incomingHelpRequestExpiryTimer.Start();
        incomingHelpRequestTimeoutCts = new CancellationTokenSource();
        var ct = incomingHelpRequestTimeoutCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(approvalTimeout, ct).ConfigureAwait(false);
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
                await sessionRuntime.RejectIncomingHelpRequestAsync("request_timeout", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LocalOperationalLog.Warn(
                    "DirectHelpRequest",
                    $"event=help_request_timeout_reject_failed; reason=decision_send_failed; exception_type={ex.GetType().Name}; role=Helper; runtime_state={sessionRuntime.State}; transport_state={sessionRuntime.TransportLifecycleState}");
            }

            await UiThreadDispatch.RunAsync(() =>
            {
                CancelIncomingHelpRequestTimeout();
                NotifyHelpRequestPresentationChanged();
                NotifyRegenerateHelperIdentityStateChanged();
                RefreshCommandStates();
                TryShowUiRecoveryTransient("incoming_help_request_timeout", "The help request expired.", canCancel: false);
                SyncTransientStatusFromRuntime();
            }).ConfigureAwait(false);
        });
    }

    private void CancelIncomingHelpRequestTimeout()
    {
        incomingHelpRequestExpiryTimer.Stop();
        incomingHelpRequestTimeoutCts?.Cancel();
        incomingHelpRequestTimeoutCts?.Dispose();
        incomingHelpRequestTimeoutCts = null;
        incomingHelpRequestExpiresAtUtc = DateTimeOffset.MinValue;
        UpdateIncomingHelpRequestTimeoutText(string.Empty);
    }

    private void OnIncomingHelpRequestExpiryTimerTick(object? sender, EventArgs e)
    {
        if (!HasPendingHelpRequest ||
            incomingHelpRequestExpiresAtUtc == DateTimeOffset.MinValue)
        {
            CancelIncomingHelpRequestTimeout();
            NotifyHelpRequestPresentationChanged();
            return;
        }

        UpdateIncomingHelpRequestTimeoutText(BuildIncomingHelpRequestTimeoutText(nowProvider()));
    }

    private string BuildIncomingHelpRequestTimeoutText(DateTimeOffset now)
    {
        if (incomingHelpRequestExpiresAtUtc == DateTimeOffset.MinValue)
        {
            return string.Empty;
        }

        var remaining = incomingHelpRequestExpiresAtUtc - now;
        var totalSeconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;
        return $"Request expires in {minutes:D2}:{seconds:D2}.";
    }

    private void UpdateIncomingHelpRequestTimeoutText(string value)
    {
        value ??= string.Empty;
        if (SetProperty(ref incomingHelpRequestTimeoutText, value, nameof(IncomingHelpRequestTimeoutText)))
        {
            OnPropertyChanged(nameof(ShowIncomingHelpRequestTimeout));
        }
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
        return CanEndSession && !localEndCommandInFlight;
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
                await HandlePendingApprovalTimeoutAsync().ConfigureAwait(false);
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

        if (!disposed && !IsStartupBlocked)
        {
            await StartListeningAsync().ConfigureAwait(false);
        }
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

        if (localEndCommandInFlight)
        {
            return;
        }

        localEndCommandInFlight = true;
        EndSessionCommand.NotifyCanExecuteChanged();
        sessionRuntime.NotifyLocalEndRequested();
        connectCts?.Cancel();
        connectOutcome?.TrySetCanceled();
        IsConnecting = false;
        uiRecoveryTransientDismissed = true;
        ClearUiRecoveryTransient();
        ShowTransientBanner = false;
        TransientBannerText = string.Empty;
        CanCancelTransient = false;
        ApplyTerminalPresentationFromFlow(sessionRuntime.FlowSnapshot);
        uiStateStore?.SetPhase(SessionUiPhase.Waiting, "UserEndSession:ReturnToWaiting");
        EffectivePhase = SessionUiPhase.Waiting;
        SendChatCommand.NotifyCanExecuteChanged();
        EndSessionCommand.NotifyCanExecuteChanged();
        AssertUiConsistency();
        ClearRemoteScreenShareFrame();

        _ = DisconnectAfterLocalEndAsync();
    }

    private async Task HandlePendingApprovalTimeoutAsync()
    {
        LogReliability(SessionReliabilityStage.Disconnected, "approval_timeout", "No response yet.");

        await sessionRuntime.HandleHelperApprovalTimeoutAsync().ConfigureAwait(false);
        await UiThreadDispatch.RunAsync(() => OnPropertyChanged(nameof(ShowChatConnectionHint)));
    }

    private async Task DisconnectAfterLocalEndAsync()
    {
        try
        {
            await sessionRuntime.DisconnectAsync().ConfigureAwait(false);
            if (!disposed && !IsStartupBlocked)
            {
                await sessionRuntime.StartHelperListeningAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Helper local end-session disconnect failed: {ex.Message}");
        }
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
                LogRemoteControlInputUi(
                    "remote_control_input_ui_ignored",
                    "keyboard_capture_disabled",
                    kind);
                return;
            }
        }
        else if (!IsRemoteControlInputCaptureEnabled)
        {
            LogRemoteControlInputUi(
                "remote_control_input_ui_ignored",
                "pointer_capture_disabled",
                kind);
            return;
        }

        TrackRemoteControlDebugMetrics(message);
        LogRemoteControlInputUi(
            "remote_control_input_ui_forwarded",
            "ok",
            kind);
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
        LogSendFileActionUi("file_transfer_send_ui_requested", "(entry)");
        if (!CanExecuteSendFileAction())
        {
            LogSendFileActionUi("file_transfer_send_ui_ignored", "can_execute_false");
            return;
        }

        if (!sessionRuntime.TryAuthorizeFileTransferSend())
        {
            LogSendFileActionUi("file_transfer_send_ui_ignored", "authorize_false");
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
        LogFileTransferActionUi(
            "file_transfer_accept_ui_requested",
            normalizedTransferId,
            includeDecline: false);
        if (!CanAcceptIncomingFile(normalizedTransferId))
        {
            LogFileTransferActionUi(
                "file_transfer_accept_ui_ignored",
                normalizedTransferId,
                includeDecline: false);
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
        LogFileTransferActionUi(
            "file_transfer_decline_ui_requested",
            normalizedTransferId,
            includeDecline: true);
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
        LogFileTransferCancelUi("file_transfer_cancel_ui_requested", normalizedTransferId);
        if (!CanCancelFileTransfer(normalizedTransferId))
        {
            LogFileTransferCancelUi("file_transfer_cancel_ui_ignored", normalizedTransferId);
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

    private async Task PauseFileTransferAsync(string? transferId)
    {
        var normalizedTransferId = NormalizeTransferActionId(transferId);
        LogFileTransferPauseResumeUi("file_transfer_pause_ui_requested", normalizedTransferId);
        if (!CanPauseFileTransfer(normalizedTransferId))
        {
            LogFileTransferPauseResumeUi("file_transfer_pause_ui_ignored", normalizedTransferId);
            return;
        }

        await sessionRuntime.PauseTransferAsync(normalizedTransferId!, "ui_pause", CancellationToken.None).ConfigureAwait(false);
    }

    private bool CanPauseFileTransfer(string? transferId)
    {
        var normalizedTransferId = NormalizeTransferActionId(transferId);
        if (normalizedTransferId is null)
        {
            return false;
        }

        return (InboundFileTransfer is { ShowPause: true } inbound &&
                string.Equals(inbound.TransferId, normalizedTransferId, StringComparison.Ordinal)) ||
               (OutboundFileTransfer is { ShowPause: true } outbound &&
                string.Equals(outbound.TransferId, normalizedTransferId, StringComparison.Ordinal));
    }

    private async Task ResumeFileTransferAsync(string? transferId)
    {
        var normalizedTransferId = NormalizeTransferActionId(transferId);
        LogFileTransferPauseResumeUi("file_transfer_resume_ui_requested", normalizedTransferId);
        if (!CanResumeFileTransfer(normalizedTransferId))
        {
            LogFileTransferPauseResumeUi("file_transfer_resume_ui_ignored", normalizedTransferId);
            return;
        }

        await sessionRuntime.ResumeTransferAsync(normalizedTransferId!, "ui_resume", CancellationToken.None).ConfigureAwait(false);
    }

    private bool CanResumeFileTransfer(string? transferId)
    {
        var normalizedTransferId = NormalizeTransferActionId(transferId);
        if (normalizedTransferId is null)
        {
            return false;
        }

        return (InboundFileTransfer is { ShowResume: true } inbound &&
                string.Equals(inbound.TransferId, normalizedTransferId, StringComparison.Ordinal)) ||
               (OutboundFileTransfer is { ShowResume: true } outbound &&
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
            await clipboardService.SetTextAsync(GetHelperBootstrapShareValue());
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
            var shared = await inviteShareService.ShareInviteAsync(GetHelperBootstrapShareValue(), CancellationToken.None);
            copyFeedback.Show(shared.IsSuccess
                ? "Helper address shared."
                : shared.Message ?? "Could not share. Please try again.");
        }
        catch
        {
            copyFeedback.Show("Could not share. Please try again.");
        }
    }

    private async Task RegenerateHelperIdentityAsync()
    {
        if (!CanRegenerateHelperIdentity)
        {
            NotifyRegenerateHelperIdentityStateChanged();
            return;
        }

        helperIdentityRegenerationInFlight = true;
        NotifyRegenerateHelperIdentityStateChanged();
        copyFeedback.Show("Regenerating helper address...");

        try
        {
            CancelBootstrapHelperIdentityResolution();
            await AwaitBootstrapHelperIdentityResolutionCompletionAsync().ConfigureAwait(false);

            if (sessionRuntime.State != SessionRuntimeState.Idle ||
                sessionRuntime.TransportLifecycleState != TransportState.Idle)
            {
                await sessionRuntime.DisconnectAsync().ConfigureAwait(false);
            }

            var regenerate = regenerateHelperIdentityAsync ??
                throw new InvalidOperationException("Helper address regeneration is not available for this transport.");
            var regeneratedIdentity = await regenerate(CancellationToken.None).ConfigureAwait(false);

            await UiThreadDispatch.RunAsync(() =>
            {
                bootstrapHelperIdentity = regeneratedIdentity;
                bootstrapHelperIdentityIsAuthoritative = false;
                lastPublishedHelperBootstrapSnapshotKey = string.Empty;
                helperIdentityBootstrapErrorText = string.Empty;
                helperIdentityBootstrapPending = false;
                NotifyHelperIdentityBootstrapChanged();
            });

            await StartListeningAsync().ConfigureAwait(false);
            await UiThreadDispatch.RunAsync(() => copyFeedback.Show("Helper address regenerated."));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Helper address regeneration failed: {ex.Message}");
            await UiThreadDispatch.RunAsync(() =>
            {
                helperIdentityBootstrapErrorText = "Could not regenerate helper address. Try again.";
                helperIdentityBootstrapPending = false;
                NotifyHelperIdentityBootstrapChanged();
                copyFeedback.Show("Could not regenerate helper address.");
            });
        }
        finally
        {
            await UiThreadDispatch.RunAsync(() =>
            {
                helperIdentityRegenerationInFlight = false;
                NotifyRegenerateHelperIdentityStateChanged();
            });
        }
    }

    private async Task StartListeningAsync()
    {
        if (disposed || IsStartupBlocked)
        {
            return;
        }

        // State changes are marshalled onto the UI thread, so a queued auto-listen refresh can
        // run after the runtime has already pivoted from idle listener mode into an active helper
        // connect flow. Do not let that stale callback reset an in-flight accepted request.
        if (sessionRuntime.Role != SessionRuntimeRole.None ||
            sessionRuntime.State != SessionRuntimeState.Idle ||
            sessionRuntime.TransportLifecycleState != TransportState.Idle)
        {
            return;
        }

        try
        {
            await sessionRuntime.StartHelperListeningAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            await UiThreadDispatch.RunAsync(() =>
            {
                StatusText = "Could not start helper listening.";
                ConnectionState = "Failed";
            });
        }
    }

    private async Task AcceptHelpRequestAsync()
    {
        if (!CanRespondToHelpRequest())
        {
            return;
        }

        try
        {
            CancelIncomingHelpRequestTimeout();
            await sessionRuntime.AcceptIncomingHelpRequestAsync(CancellationToken.None).ConfigureAwait(false);
            await UiThreadDispatch.RunAsync(NotifyHelpRequestPresentationChanged).ConfigureAwait(false);
        }
        catch
        {
            throw;
        }
    }

    private async Task RejectHelpRequestAsync()
    {
        if (!CanRespondToHelpRequest())
        {
            return;
        }

        CancelIncomingHelpRequestTimeout();
        await sessionRuntime.RejectIncomingHelpRequestAsync("request_rejected", CancellationToken.None).ConfigureAwait(false);
        await UiThreadDispatch.RunAsync(NotifyHelpRequestPresentationChanged).ConfigureAwait(false);
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
        helperIdentityBootstrapPending = true;
        if (bootstrapHelperIdentityResolver is null)
        {
            bootstrapHelperIdentityResolutionTask = null;
            NotifyHelperIdentityBootstrapChanged();
            return;
        }

        bootstrapHelperIdentityResolutionCts = new CancellationTokenSource();
        bootstrapHelperIdentityResolutionTask = ResolveBootstrapHelperIdentityAsync(bootstrapHelperIdentityResolutionCts.Token);
    }

    private void NotifyHelperIdentityBootstrapChanged()
    {
        NotifyHelperBootstrapPropertiesChanged();
        RefreshHelperBootstrapQrBitmap();
    }

    private void NotifyHelperBootstrapPropertiesChanged()
    {
        OnPropertyChanged(nameof(HelperIdentityBootstrapText));
        OnPropertyChanged(nameof(HasReadyHelperIdentityBootstrapText));
        OnPropertyChanged(nameof(HelperIdentityBootstrapWatermarkText));
        OnPropertyChanged(nameof(HelperIdentityBootstrapHintText));
        OnPropertyChanged(nameof(HelperIdentityBootstrapVerificationCode));
        OnPropertyChanged(nameof(HasHelperIdentityBootstrapVerificationCode));
        OnPropertyChanged(nameof(ShowHelperIdentityBootstrapPanel));
        OnPropertyChanged(nameof(HeaderVerificationCodeText));
        OnPropertyChanged(nameof(ShowHeaderVerificationCode));
        OnPropertyChanged(nameof(FirstPillVerificationCodeText));
        OnPropertyChanged(nameof(ShowFirstPillVerificationCode));
        NotifySessionVerificationPropertiesChanged();
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
            bootstrapHelperIdentity.Value != verifiedIdentity ||
            !bootstrapHelperIdentityIsAuthoritative;

        bootstrapHelperIdentity = verifiedIdentity;
        bootstrapHelperIdentityIsAuthoritative = true;
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
        var snapshot = sessionRuntime.CurrentHelperListenerBootstrapSnapshot;
        if (snapshot is null)
        {
            if (!string.IsNullOrWhiteSpace(lastPublishedHelperBootstrapSnapshotKey))
            {
                lastPublishedHelperBootstrapSnapshotKey = string.Empty;
            }

            return;
        }

        var resolvedIdentity = snapshot.Address;
        var snapshotKey = string.Create(
            CultureInfo.InvariantCulture,
            $"{snapshot.RunId}|{snapshot.ListenerGeneration}|{resolvedIdentity.Value}");
        if (!string.Equals(lastPublishedHelperBootstrapSnapshotKey, snapshotKey, StringComparison.Ordinal))
        {
            lastPublishedHelperBootstrapSnapshotKey = snapshotKey;
        }

        var changed =
            bootstrapHelperIdentity is null ||
            bootstrapHelperIdentity.Value != resolvedIdentity ||
            !bootstrapHelperIdentityIsAuthoritative;
        bootstrapHelperIdentity = resolvedIdentity;
        bootstrapHelperIdentityIsAuthoritative = true;
        helperIdentityBootstrapErrorText = string.Empty;
        helperIdentityBootstrapPending = false;
        CancelBootstrapHelperIdentityResolution();
        bootstrapHelperIdentityResolutionTask = null;
        if (changed)
        {
            NotifyHelperIdentityBootstrapChanged();
        }
        else
        {
            OnPropertyChanged(nameof(HelperIdentityBootstrapHintText));
            OnPropertyChanged(nameof(ShowHelperIdentityBootstrapPanel));
        }
    }

    private void RefreshHelperBootstrapQrBitmap()
    {
        var payload = GetHelperBootstrapShareValue();
        if (string.Equals(lastHelperBootstrapQrPayload, payload, StringComparison.Ordinal) &&
            ((helperBootstrapQrBitmap is not null) || string.IsNullOrWhiteSpace(payload)))
        {
            OnPropertyChanged(nameof(HelperBootstrapQrImage));
            OnPropertyChanged(nameof(ShowHelperBootstrapQr));
            OnPropertyChanged(nameof(ShowHelperBootstrapQrPlaceholder));
            return;
        }

        lastHelperBootstrapQrPayload = payload;
        helperBootstrapQrBitmap?.Dispose();
        helperBootstrapQrBitmap = null;

        if (string.IsNullOrWhiteSpace(payload))
        {
            OnPropertyChanged(nameof(HelperBootstrapQrImage));
            OnPropertyChanged(nameof(ShowHelperBootstrapQr));
            OnPropertyChanged(nameof(ShowHelperBootstrapQrPlaceholder));
            return;
        }

        if (qrCodeService.TryCreatePng(payload, out var pngBytes, out var errorMessage))
        {
            try
            {
                using var stream = new System.IO.MemoryStream(pngBytes, writable: false);
                helperBootstrapQrBitmap = new Bitmap(stream);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException)
            {
                LocalOperationalLog.Warn(
                    "HelperUi",
                    $"event=helper_bootstrap_qr_decode_failed; payload_len={payload.Length}; reason={ex.GetType().Name}");
                helperBootstrapQrBitmap = null;
            }
        }
        else
        {
            LocalOperationalLog.Warn(
                "HelperUi",
                $"event=helper_bootstrap_qr_generation_failed; payload_len={payload.Length}; reason={(string.IsNullOrWhiteSpace(errorMessage) ? "unknown" : errorMessage)}");
        }

        OnPropertyChanged(nameof(HelperBootstrapQrImage));
        OnPropertyChanged(nameof(ShowHelperBootstrapQr));
        OnPropertyChanged(nameof(ShowHelperBootstrapQrPlaceholder));
    }

    private string BuildHelperBootstrapDisplayValue()
    {
        if (HelperRequestTargetAddress is { } helperTargetAddress)
        {
            return HelperBootstrapQrPayload.Format(
                HelperBootstrapPayload.Create(
                    helperTargetAddress,
                    helperId: HelperCanonicalBootstrapVerificationIdentity is { } helperIdentity
                        ? HelperIdentityTokenCodec.Encode(helperIdentity)
                        : null));
        }

        return HelperIdentityForDisplay is { } helperIdentityForDisplay
            ? helperIdentityForDisplay.Value
            : string.Empty;
    }

    private string GetHelperBootstrapShareValue()
    {
        return BuildHelperBootstrapDisplayValue();
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
        var resolver = bootstrapHelperIdentityResolver;
        if (resolver is null)
        {
            return;
        }

        try
        {
            var resolved = await resolver(ct).ConfigureAwait(false);
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
                    bootstrapHelperIdentityIsAuthoritative = false;
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
            if (approvalTimedOut)
            {
                LogReliability(SessionReliabilityStage.Disconnected, "approval_timeout", "No response yet.");
            }
            else
            {
                LogReliability(SessionReliabilityStage.Rejected, "rejected", "They did not allow the connection.");
            }
            SyncFromRuntime();
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
            var (errorCode, errorHint) = GetReliabilityError();
            LogReliability(SessionReliabilityStage.Disconnected, errorCode, errorHint);
            SyncFromRuntime();
            if (sessionRuntime.LastDisconnectWasRemoteEnd)
            {
                ShowPeerEndedNotice("The other person ended the session.");
                IsChatInputEnabled = false;
                CanSendFiles = false;
                CanEndSession = false;
                ShowChatNotice = false;
                LocalOperationalLog.Info(
                    "HelperUi",
                    $"event=helper_remote_session_end_notice_latched; source=disconnected; last_remote={(sessionRuntime.LastDisconnectWasRemoteEnd ? 1 : 0)}; header={SanitizeForLog(HeaderStatusText)}; phase={EffectivePhase}; runtime_state={sessionRuntime.State}; chat_input={(IsChatInputEnabled ? 1 : 0)}");
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
            ClearRemoteScreenShareFrame();
            IsConnecting = false;
            SyncFromRuntime();
            ShowPeerEndedNotice("The other person ended the session.");
            IsChatInputEnabled = false;
            CanSendFiles = false;
            CanEndSession = false;
            ShowChatNotice = false;
            LocalOperationalLog.Info(
                "HelperUi",
                $"event=helper_remote_session_end_notice_latched; source=remote_session_ended; last_remote={(sessionRuntime.LastDisconnectWasRemoteEnd ? 1 : 0)}; header={SanitizeForLog(HeaderStatusText)}; phase={EffectivePhase}; runtime_state={sessionRuntime.State}; chat_input={(IsChatInputEnabled ? 1 : 0)}");
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
            UpdateUiFromSnapshot("chat_state_changed");
            LogCurrentChatPanelState("chat_state_changed");
        });
    }

    private void OnFileTransferChanged(object? sender, EventArgs e)
    {
        if (disposed)
        {
            return;
        }

        ScheduleFileTransferUiRefresh(e as SessionFileTransferSnapshotChangedEventArgs);
    }

    private void ScheduleFileTransferUiRefresh(SessionFileTransferSnapshotChangedEventArgs? e)
    {
        if (disposed)
        {
            return;
        }

        Interlocked.Increment(ref fileTransferUiRefreshPendingCount);
        if (e?.Snapshot is { } snapshot)
        {
            Interlocked.Exchange(ref pendingFileTransferUiSnapshot, snapshot);
        }

        if (IsUrgentFileTransferSnapshot(e?.Snapshot))
        {
            Volatile.Write(ref fileTransferUiRefreshUrgent, 1);
        }

        if (Interlocked.CompareExchange(ref fileTransferUiRefreshScheduled, 1, 0) != 0)
        {
            return;
        }

        _ = RunFileTransferUiRefreshLoopAsync();
    }

    private async Task RunFileTransferUiRefreshLoopAsync()
    {
        try
        {
            while (!disposed)
            {
                var urgent = Interlocked.Exchange(ref fileTransferUiRefreshUrgent, 0) != 0;
                if (!urgent)
                {
                    var elapsedMs = Environment.TickCount64 - Volatile.Read(ref lastFileTransferUiRefreshUtcMs);
                    var delayMs = FileTransferUiRefreshMinIntervalMs - Math.Max(0, elapsedMs);
                    if (delayMs > 0)
                    {
                        await Task.Delay((int)delayMs).ConfigureAwait(false);
                    }
                }

                var coalescedCount = Interlocked.Exchange(ref fileTransferUiRefreshPendingCount, 0);
                if (coalescedCount > 0)
                {
                    var snapshotOverride = Interlocked.Exchange(ref pendingFileTransferUiSnapshot, null);
                    await UiThreadDispatch.RunAsync(() =>
                    {
                        if (disposed)
                        {
                            return;
                        }

                        UpdateUiFromSnapshot(
                            coalescedCount > 1 ? "file_transfer_changed_coalesced" : "file_transfer_changed",
                            snapshotOverride);
                    }).ConfigureAwait(false);
                    Volatile.Write(ref lastFileTransferUiRefreshUtcMs, Environment.TickCount64);
                    if (coalescedCount >= FileTransferUiRefreshCoalescedLogThreshold)
                    {
                        LocalOperationalLog.Info(
                            "HelperUi",
                            $"event=file_transfer_ui_refresh_coalesced; role=Helper; coalesced_count={coalescedCount}; min_interval_ms={FileTransferUiRefreshMinIntervalMs}");
                    }
                }

                if (Volatile.Read(ref fileTransferUiRefreshPendingCount) == 0)
                {
                    Interlocked.Exchange(ref fileTransferUiRefreshScheduled, 0);
                    if (Volatile.Read(ref fileTransferUiRefreshPendingCount) == 0 ||
                        Interlocked.CompareExchange(ref fileTransferUiRefreshScheduled, 1, 0) != 0)
                    {
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref fileTransferUiRefreshScheduled, 0);
            LocalOperationalLog.Warn("HelperUi", $"event=file_transfer_ui_refresh_failed; role=Helper; error={ex.GetType().Name}");
        }
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

    private void OnFlowSnapshotChanged(object? sender, SessionFlowSnapshotChangedEventArgs e)
    {
        if (disposed)
        {
            return;
        }

        _ = UiThreadDispatch.RunAsync(SyncFromRuntime);
    }

    private void OnSessionSecurityStateChanged(object? sender, EventArgs e)
    {
        if (disposed)
        {
            return;
        }

        _ = UiThreadDispatch.RunAsync(SyncFromRuntime);
    }

    private void OnTransportAccelerationStateChanged(object? sender, EventArgs e)
    {
        if (disposed)
        {
            return;
        }

        _ = UiThreadDispatch.RunAsync(SyncTunaActiveFromRuntime);
    }

    private void SyncTunaActiveFromRuntime()
    {
        IsTunaActive = sessionRuntime.IsTransportAccelerationActive;
        TunaStatusReason = sessionRuntime.TransportAccelerationStatusReason;
    }

    private void OnHelperListenerBootstrapSnapshotChanged(object? sender, EventArgs e)
    {
        if (disposed)
        {
            return;
        }

        _ = UiThreadDispatch.RunAsync(SyncFromRuntime);
    }

    private void OnScreenShareFrameCompleted(object? sender, ScreenShareFrameCompletedEventArgs e)
    {
        try
        {
            ScreenShareViewer.OnOwnedEncodedFrame(
                e.Encoding,
                e.EncodedFrameBytes,
                e.CapturedTsUtcMs,
                e.IsKeyFrame,
                e.StreamEpoch,
                e.StreamConfig,
                e.ChunksDroppedOlderFrame,
                e.AssembliesExpired,
                e.FrameId,
                e.SessionId,
                e.RecoveryDeliveryClass,
                e.FrameReadyObservedUtcMs);
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Info(
                "HelperUi",
                $"event=helper_screenshare_frame_dispatch_failed; stage=viewer_enqueue; stream_epoch={e.StreamEpoch}; frame_id={e.FrameId}; is_keyframe={(e.IsKeyFrame ? 1 : 0)}; encoding={e.Encoding}; reason={ex.GetType().Name}; message={SanitizeScreenShareDispatchExceptionMessage(ex.Message)}");
            throw;
        }
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

    private void OnScreenShareCursorStateReceived(object? sender, ScreenShareCursorStateReceivedEventArgs e)
    {
        if (disposed)
        {
            return;
        }

        _ = UiThreadDispatch.RunAsync(() =>
        {
            ScreenShareViewer.OnCursorState(e.Message);
        });
    }

    private static string SanitizeScreenShareDispatchExceptionMessage(string? message)
    {
        return string.IsNullOrWhiteSpace(message)
            ? "(none)"
            : message.Replace(';', ',').Trim();
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
            var previousShowViewerError = lastKnownShowScreenShareViewerError;
            var previousViewerMessage = lastKnownScreenShareViewerMessage;

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
                    helperRemoteSurfaceVisibleLogged = false;
                    ResetRemoteControlDebugMetrics();
                }
            }

            if (nextShowRemoteScreenShareFrame && !helperRemoteSurfaceVisibleLogged)
            {
                LocalOperationalLog.Info(
                    "HelperUi",
                    $"event=helper_screenshare_viewer_surface_visible; role=helper_remote; control_state={sessionRuntime.ControlState}; header_status={HeaderStatusText}; viewer_status={ScreenShareViewer.StatusText}");
                helperRemoteSurfaceVisibleLogged = true;
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
                if (ShowScreenShareViewerError)
                {
                    LocalOperationalLog.Info(
                        "HelperUi",
                        $"event=helper_screenshare_viewer_error_visible; role=helper_remote; control_state={sessionRuntime.ControlState}; header_status={HeaderStatusText}; message={SanitizeForLog(ScreenShareViewerMessage)}");
                }
                lastKnownShowScreenShareViewerError = ShowScreenShareViewerError;
            }

            if (!string.Equals(previousViewerMessage, ScreenShareViewerMessage, StringComparison.Ordinal))
            {
                OnPropertyChanged(nameof(ScreenShareViewerMessage));
                lastKnownScreenShareViewerMessage = ScreenShareViewerMessage;
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

    private void OnScreenShareViewerFrameApplied(object? sender, ScreenShareViewerFrameAppliedEventArgs e)
    {
        if (disposed)
        {
            return;
        }

        var helperSessionSnapshot = ScreenShareViewer.GetHelperRemoteSessionSnapshot();
        sessionRuntime.ReportHelperRemoteScreenShareFrameApplied(
            e.AgeMs,
            e.StreamEpoch,
            e.FrameId,
            e.VisibleHeadFrameId,
            e.StableVisibleHeadFrameId,
            e.FramesAppliedSinceLastGap,
            helperSessionSnapshot);
    }

    private void OnScreenShareViewerStaleFrameDropped(object? sender, ScreenShareViewerStaleFrameDroppedEventArgs e)
    {
        if (disposed)
        {
            return;
        }

        sessionRuntime.ReportHelperRemoteScreenShareStaleFrameDropped(
            e.RenderedAgeMs,
            e.StreamEpoch,
            e.ReferenceContinuityPreserved);
    }

    private void OnScreenShareViewerDecodeNeedsMoreInput(object? sender, ScreenShareViewerDecodeNeedsMoreInputEventArgs e)
    {
        if (disposed)
        {
            return;
        }

        sessionRuntime.ReportHelperRemoteScreenShareDecodeNeedsMoreInput(e.StreamEpoch);
        sessionRuntime.ReportHelperRemoteScreenShareSessionSnapshot(ScreenShareViewer.GetHelperRemoteSessionSnapshot());
    }

    private void OnScreenShareViewerContinuityLost(object? sender, ScreenShareViewerContinuityLostEventArgs e)
    {
        if (disposed)
        {
            return;
        }

        sessionRuntime.ReportHelperRemoteScreenShareContinuityLost(
            e.StreamEpoch,
            e.Reason,
            e.ShouldRequestRecoveryKeyframe,
            e.CurrentEpochNeedMoreInputCount,
            e.ExpectedNextFrameId,
            e.ReceivedFrameId,
            e.LastCleanFrameId);
        sessionRuntime.ReportHelperRemoteScreenShareSessionSnapshot(ScreenShareViewer.GetHelperRemoteSessionSnapshot());
    }

    private void OnScreenShareViewerRecoveryKeyframeApplied(object? sender, ScreenShareViewerRecoveryKeyframeAppliedEventArgs e)
    {
        if (disposed)
        {
            return;
        }

        sessionRuntime.ReportHelperRemoteScreenShareRecoveryKeyframeApplied(e.AgeMs, e.StreamEpoch);
        sessionRuntime.ReportHelperRemoteScreenShareSessionSnapshot(ScreenShareViewer.GetHelperRemoteSessionSnapshot());
    }

    private void OnScreenShareViewerRecoveryWindowStateChanged(object? sender, ScreenShareViewerRecoveryWindowStateChangedEventArgs e)
    {
        if (disposed)
        {
            return;
        }

        sessionRuntime.ReportHelperRemoteScreenShareRecoveryWindowStateChanged(
            e.StreamEpoch,
            e.RecoveryFrameId,
            e.LastContiguousFrameId,
            e.ContiguousFollowerApplyCount,
            e.Status,
            e.AbortReason);
        sessionRuntime.ReportHelperRemoteScreenShareSessionSnapshot(ScreenShareViewer.GetHelperRemoteSessionSnapshot());
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
                     sessionRuntime.FlowSnapshot.UiPhase == SessionUiPhase.Failed)
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
        NotifyRegenerateHelperIdentityStateChanged();
        var flow = sessionRuntime.FlowSnapshot;
        if (suppressRetryActionForReturnToWaiting &&
            (flow.TerminalKind == SessionTerminalKind.None ||
             flow.Phase is SessionFlowPhase.ListenerWaiting or SessionFlowPhase.NoSession or SessionFlowPhase.ActiveSession))
        {
            suppressRetryActionForReturnToWaiting = false;
            helperListenerReturnToWaitingRequested = false;
        }
        SyncConversationBoundaryFromFlow(flow);

        if (localEndCommandInFlight &&
            !flow.LocalEndInProgress &&
            flow.Phase is SessionFlowPhase.ListenerWaiting or SessionFlowPhase.NoSession)
        {
            PrepareForNewSession(clearConnectInput: false);
        }

        if (flow.PostTerminalAction == SessionFlowPostTerminalAction.ReturnToListenerWaiting)
        {
            if (flow.TerminalKind is SessionTerminalKind.Failed or SessionTerminalKind.Rejected)
            {
                var transientText = !string.IsNullOrWhiteSpace(flow.TerminalStatusText)
                    ? flow.TerminalStatusText
                    : BuildRecoveryTransientText(isRecovering: false);
                TryShowUiRecoveryTransient(
                    $"post-terminal:{flow.TerminalKind}:{flow.FailureReason}",
                    transientText,
                    canCancel: false);
            }

            ReturnToListenerWaiting(clearConnectInput: false);
            SyncTransientStatusFromRuntime();
            NotifyDisconnectedUiAffordancesChanged();
            return;
        }

        if (ShouldReturnHelperApprovalTimeoutToWaiting(flow))
        {
            TryShowUiRecoveryTransient(
                $"approval-timeout:{flow.SessionId ?? "(none)"}",
                !string.IsNullOrWhiteSpace(flow.TerminalStatusText)
                    ? flow.TerminalStatusText
                    : UserErrorMapper.HelperApprovalTimeout(),
                canCancel: false);
            ReturnToListenerWaiting(clearConnectInput: false);
            SyncTransientStatusFromRuntime();
            NotifyDisconnectedUiAffordancesChanged();
            return;
        }

        if (flow.TerminalKind != SessionTerminalKind.None)
        {
            ApplyTerminalPresentationFromFlow(flow);
        }
        else
        {
            ClearFailurePresentation();
            StatusText = SessionFlowViewProjection.ResolveStatusText(flow, transportConfig.ApprovedStatusText);
            ConnectionState = flow.DisplayConnectionState;
            IsConnecting = string.Equals(flow.DisplayConnectionState, "Connecting", StringComparison.Ordinal);

            if (string.Equals(flow.DisplayConnectionState, "Connected", StringComparison.Ordinal))
            {
                ShowChatNotice = false;
            }
        }

        if (flow.UiPhase is SessionUiPhase.Connected or SessionUiPhase.Waiting or SessionUiPhase.Idle)
        {
            if (!sessionRuntime.IsTransientStatusVisible)
            {
                uiRecoveryTransientDismissed = false;
            }
            ClearUiRecoveryTransient();
        }

        if (flow.ShouldClearConversationUi)
        {
            ClearRemoteScreenShareFrame();
        }

        SessionUxContext? phaseContext = null;
        if (flow.UiPhase == SessionUiPhase.Failed &&
            (!string.IsNullOrWhiteSpace(flow.FailureTitle) ||
             !string.IsNullOrWhiteSpace(flow.FailureMessage) ||
             !string.IsNullOrWhiteSpace(flow.FailureActionText)))
        {
            phaseContext = new SessionUxContext(flow.FailureTitle, flow.FailureMessage, flow.FailureActionText);
        }

        uiStateStore?.SetPhase(flow.UiPhase, $"SyncFromRuntime:Flow:{flow.Phase}", phaseContext);
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
        OnPropertyChanged(nameof(ShowHelperSetupPanel));
        OnPropertyChanged(nameof(ShowCopyFeedbackInline));
        OnPropertyChanged(nameof(HeaderStatusText));
        OnPropertyChanged(nameof(HelperVerificationCode));
        OnPropertyChanged(nameof(HasHelperVerificationCode));
        OnPropertyChanged(nameof(ShowHelperVerificationCode));
        OnPropertyChanged(nameof(HeaderVerificationCodeText));
        OnPropertyChanged(nameof(ShowHeaderVerificationCode));
        OnPropertyChanged(nameof(FirstPillVerificationCodeText));
        OnPropertyChanged(nameof(ShowFirstPillVerificationCode));
        NotifySessionVerificationPropertiesChanged();
        NotifyHelperBootstrapPropertiesChanged();
        RefreshHelperBootstrapQrBitmap();
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
        RefreshCommandStates();
        EnsureControlModeConsistency();
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

        if (showPeerEndedNotice)
        {
            ShowTransientBanner = true;
            TransientBannerText = peerEndedNoticeText;
            CanCancelTransient = false;
            return;
        }

        if (sessionRuntime.IsTransientStatusVisible)
        {
            if (ShouldSuppressPassiveListeningTransient(sessionRuntime.TransientStatusText))
            {
                ShowTransientBanner = false;
                TransientBannerText = string.Empty;
                CanCancelTransient = false;
                return;
            }

            var suppressAfterUserCancel =
                uiRecoveryTransientDismissed &&
                !IsConnecting &&
                sessionRuntime.FlowSnapshot.UiPhase != SessionUiPhase.Connecting;
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
            if (ShouldSuppressPassiveListeningTransient(uiRecoveryTransientText))
            {
                ShowTransientBanner = false;
                TransientBannerText = string.Empty;
                CanCancelTransient = false;
                return;
            }

            ShowTransientBanner = true;
            TransientBannerText = SanitizeTransientText(uiRecoveryTransientText);
            CanCancelTransient = uiRecoveryTransientCanCancel;
            return;
        }

        ShowTransientBanner = false;
        TransientBannerText = string.Empty;
        CanCancelTransient = false;
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
        var phase = sessionRuntime.FlowSnapshot.UiPhase;
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

    private static bool IsUrgentFileTransferSnapshot(SessionFileTransferSnapshot? snapshot)
        => IsUrgentFileTransferItem(snapshot?.Inbound) ||
           IsUrgentFileTransferItem(snapshot?.Outbound);

    private static bool IsUrgentFileTransferItem(FileTransferTransferSnapshot? snapshot)
        => snapshot is not null &&
           (snapshot.IsTerminal ||
            snapshot.State is FileTransferTransferState.Offering
                or FileTransferTransferState.AwaitingAcceptance
                or FileTransferTransferState.PendingDecision
                or FileTransferTransferState.AwaitingMetadata
                or FileTransferTransferState.PreparingMetadata
                or FileTransferTransferState.AwaitingStart
                or FileTransferTransferState.AwaitingCompletion
                or FileTransferTransferState.Verifying ||
            snapshot.IsPaused ||
            snapshot.IsPeerPaused);

    private void UpdateUiFromSnapshot(string source, SessionFileTransferSnapshot? fileTransferSnapshotOverride = null)
    {
        bool nextChatEnabled;
        bool nextCanSendFiles;
        bool nextCanEndSession;
        bool nextCanOpenDiagnostics;
        var flow = sessionRuntime.FlowSnapshot;
        var fileTransferSnapshot = fileTransferSnapshotOverride ?? sessionRuntime.FileTransferSnapshot;
        InboundFileTransfer = FileTransferPanelItemViewModel.FromSnapshot(
            fileTransferSnapshot.Inbound,
            AcceptIncomingFileCommand,
            DeclineIncomingFileCommand,
            CancelFileTransferCommand,
            PauseFileTransferCommand,
            ResumeFileTransferCommand);
        OutboundFileTransfer = FileTransferPanelItemViewModel.FromSnapshot(
            fileTransferSnapshot.Outbound,
            cancelCommand: CancelFileTransferCommand,
            pauseCommand: PauseFileTransferCommand,
            resumeCommand: ResumeFileTransferCommand);
        var hasActiveOutboundTransfer = fileTransferSnapshot.Outbound is { IsTerminal: false };
        var phase = GetEffectivePhase();
        var suppressConnectedControlsDuringLocalEnd = flow.SuppressConnectedControls;
        var connectedForChat = flow.CanUseChatControls;
        EffectivePhase = phase;
        nextCanEndSession = !suppressConnectedControlsDuringLocalEnd && CanEndForPhase(phase);

        if (!FeatureFlags.UsePhaseDrivenGating || uiStateStore is null)
        {
            nextCanOpenDiagnostics = openDiagnosticsAction is not null && flow.ShowDiagnosticsAction;
            nextCanSendFiles = flow.IsConnectedShellVisible &&
                               sessionRuntime.CanPerform(SessionCapability.FileTransfer) &&
                               !hasActiveOutboundTransfer;
            nextChatEnabled = connectedForChat;
        }
        else
        {
            switch (phase)
            {
                case SessionUiPhase.Connected:
                    nextChatEnabled = connectedForChat;
                    nextCanSendFiles = sessionRuntime.CanPerform(SessionCapability.FileTransfer) &&
                                       !hasActiveOutboundTransfer;
                    nextCanOpenDiagnostics = openDiagnosticsAction is not null && flow.ShowDiagnosticsAction;
                    break;

                case SessionUiPhase.Connecting:
                    nextChatEnabled = connectedForChat;
                    nextCanSendFiles = false;
                    nextCanOpenDiagnostics = openDiagnosticsAction is not null && flow.ShowDiagnosticsAction;
                    break;

                case SessionUiPhase.Failed:
                case SessionUiPhase.Ended:
                    nextChatEnabled = false;
                    nextCanSendFiles = false;
                    nextCanOpenDiagnostics = openDiagnosticsAction is not null && flow.ShowDiagnosticsAction;
                    break;

                default:
                    nextChatEnabled = false;
                    nextCanSendFiles = false;
                    nextCanOpenDiagnostics = openDiagnosticsAction is not null && flow.ShowDiagnosticsAction;
                    break;
            }
        }

        if (suppressConnectedControlsDuringLocalEnd)
        {
            nextChatEnabled = false;
            nextCanSendFiles = false;
            nextCanEndSession = false;
        }

        IsChatInputEnabled = nextChatEnabled;
        CanSendFiles = nextCanSendFiles;
        CanEndSession = nextCanEndSession;
        CanOpenDiagnostics = nextCanOpenDiagnostics;

        RefreshCommandStates();
        LogCurrentChatPanelState(source);
        AssertUiConsistency();
    }

    private void RefreshCommandStates()
    {
        OnPropertyChanged(nameof(ShowOpenDiagnosticsLink));
        OnPropertyChanged(nameof(ShowSendFileAction));
        OnPropertyChanged(nameof(CanSendFileAction));

        ConnectCommand.NotifyCanExecuteChanged();
        SendFileCommand.NotifyCanExecuteChanged();
        SendChatCommand.NotifyCanExecuteChanged();
        AcceptIncomingFileCommand.NotifyCanExecuteChanged();
        DeclineIncomingFileCommand.NotifyCanExecuteChanged();
        CancelFileTransferCommand.NotifyCanExecuteChanged();
        PauseFileTransferCommand.NotifyCanExecuteChanged();
        ResumeFileTransferCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        CancelTransientCommand.NotifyCanExecuteChanged();
        OpenDiagnosticsCommand.NotifyCanExecuteChanged();
        EndSessionCommand.NotifyCanExecuteChanged();
        ScanQrFromFileCommand.NotifyCanExecuteChanged();
        ScanQrFromCameraCommand.NotifyCanExecuteChanged();
        AcceptHelpRequestCommand.NotifyCanExecuteChanged();
        RejectHelpRequestCommand.NotifyCanExecuteChanged();
        RegenerateHelperIdentityCommand.NotifyCanExecuteChanged();
        RequestControlCommand.NotifyCanExecuteChanged();
        StopControlCommand.NotifyCanExecuteChanged();
        ToggleControlModeCommand.NotifyCanExecuteChanged();
        ToggleRemoteControlDebugPanelCommand.NotifyCanExecuteChanged();
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
        WriteDebugTrace($"[HelperUi] {payload}");
    }

    private void LogFileTransferActionUi(
        string eventName,
        string? normalizedTransferId,
        bool includeDecline)
    {
        var inbound = InboundFileTransfer;
        var payload =
            $"event={eventName}; role=Helper; transfer_id_present={(normalizedTransferId is null ? 0 : 1)}; " +
            $"inbound_state={inbound?.State.ToString() ?? "(none)"}; " +
            $"inbound_show_accept={(inbound?.ShowAccept == true ? 1 : 0)}; " +
            $"effective_phase={EffectivePhase}; runtime_state={sessionRuntime.State}";
        if (includeDecline)
        {
            payload += $"; inbound_show_decline={(inbound?.ShowDecline == true ? 1 : 0)}";
        }

        LocalOperationalLog.Info("HelperUi", payload);
    }

    private void LogSendFileActionUi(string eventName, string reason)
    {
        LocalOperationalLog.Info(
            "HelperUi",
            $"event={eventName}; role=Helper; reason={reason}; " +
            $"can_send_files={(CanSendFiles ? 1 : 0)}; can_send_file_action={(CanExecuteSendFileAction() ? 1 : 0)}; " +
            $"effective_phase={EffectivePhase}; runtime_state={sessionRuntime.State}");
    }

    private void LogFileTransferCancelUi(string eventName, string? normalizedTransferId)
    {
        var inbound = InboundFileTransfer;
        var outbound = OutboundFileTransfer;
        LocalOperationalLog.Info(
            "HelperUi",
            $"event={eventName}; role=Helper; transfer_id_present={(normalizedTransferId is null ? 0 : 1)}; " +
            $"inbound_state={inbound?.State.ToString() ?? "(none)"}; inbound_show_cancel={(inbound?.ShowCancel == true ? 1 : 0)}; " +
            $"outbound_state={outbound?.State.ToString() ?? "(none)"}; outbound_show_cancel={(outbound?.ShowCancel == true ? 1 : 0)}; " +
            $"effective_phase={EffectivePhase}; runtime_state={sessionRuntime.State}");
    }

    private void LogFileTransferPauseResumeUi(string eventName, string? normalizedTransferId)
    {
        var inbound = InboundFileTransfer;
        var outbound = OutboundFileTransfer;
        LocalOperationalLog.Info(
            "HelperUi",
            $"event={eventName}; role=Helper; transfer_id_present={(normalizedTransferId is null ? 0 : 1)}; " +
            $"inbound_state={inbound?.State.ToString() ?? "(none)"}; inbound_show_pause={(inbound?.ShowPause == true ? 1 : 0)}; inbound_show_resume={(inbound?.ShowResume == true ? 1 : 0)}; " +
            $"outbound_state={outbound?.State.ToString() ?? "(none)"}; outbound_show_pause={(outbound?.ShowPause == true ? 1 : 0)}; outbound_show_resume={(outbound?.ShowResume == true ? 1 : 0)}; " +
            $"effective_phase={EffectivePhase}; runtime_state={sessionRuntime.State}");
    }

    private void LogRemoteControlInputUi(string eventName, string reason, string kind)
    {
        var normalizedKind = string.IsNullOrWhiteSpace(kind) ? "mouse_move" : kind.Trim();
        var payload =
            $"event={eventName}; role=Helper; reason={reason}; kind={SanitizeForLog(normalizedKind)}; " +
            $"input_capture_enabled={(IsRemoteControlInputCaptureEnabled ? 1 : 0)}; " +
            $"keyboard_capture_enabled={(IsRemoteControlKeyboardCaptureEnabled ? 1 : 0)}; " +
            $"mapping_available={(RemoteControlMappingAvailable ? 1 : 0)}; " +
            $"ui_connected={(IsRemoteControlUiConnected ? 1 : 0)}; " +
            $"show_frame={(ShowRemoteScreenShareFrame ? 1 : 0)}; " +
            $"control_state={sessionRuntime.ControlState}; runtime_state={sessionRuntime.State}; " +
            $"remote_control_available={(sessionRuntime.RemoteControlAvailable ? 1 : 0)}";
        if (string.Equals(payload, lastRemoteControlInputUiLog, StringComparison.Ordinal))
        {
            return;
        }

        lastRemoteControlInputUiLog = payload;
        LocalOperationalLog.Info("HelperUi", payload);
    }

    [Conditional("DEBUG")]
    private static void WriteDebugTrace(string message)
    {
        Trace.WriteLine(message);
    }

    private static bool CanEndForPhase(SessionUiPhase phase) =>
        phase is SessionUiPhase.Connecting
            or SessionUiPhase.Connected
            or SessionUiPhase.Recovering;

    private bool ShouldReturnHelperApprovalTimeoutToWaiting(SessionFlowSnapshot flow)
    {
        return flow.Role == SessionRuntimeRole.Helper &&
               flow.TerminalKind == SessionTerminalKind.Failed &&
               !flow.IsConnectedShellVisible &&
               IsApprovalTimeoutFailure();
    }

    private SessionUiPhase GetEffectivePhase()
    {
        return sessionRuntime.FlowSnapshot.UiPhase;
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
            !sessionRuntime.FlowSnapshot.IsConnectedShellVisible)
        {
            throw new InvalidOperationException("Helper UI invariant failed: chat input requires connected phase or runtime state.");
        }

        if (localEndCommandInFlight && ShowTransientBanner)
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

        if (!localEndCommandInFlight &&
            HeaderStatusText.StartsWith("Connected", StringComparison.Ordinal) &&
            sessionRuntime.FlowSnapshot.IsConnectedShellVisible &&
            sessionRuntime.CanPerform(SessionCapability.Chat) &&
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
        if (!localEndCommandInFlight)
        {
            return;
        }

        ResetToWaitingScreen(clearConnectInput);
    }

    private void ResetToWaitingScreen(bool clearConnectInput = true)
    {
        localEndCommandInFlight = false;
        lastPeerEndedNoticeKey = string.Empty;
        ClearFailurePresentation();
        StatusText = "Waiting for help requests…";
        ConnectionState = "Waiting";
        IsConnecting = false;
        ShowChatNotice = false;
        if (clearConnectInput)
        {
            CodeInput = string.Empty;
        }
        ClearSessionConversationUi();
        ClearRemoteScreenShareFrame();
        presenterBannerStatus = UserFacingStatus.IdleStatus;
        BannerStatus = presenterBannerStatus;
        if (uiStateStore is not null)
        {
            uiStateStore.SetPhase(SessionUiPhase.Waiting, "StartNewSession:Helper");
            ApplySessionBannerPolicy();
        }
        EffectivePhase = SessionUiPhase.Waiting;
    }

    private void ReturnToListenerWaiting(bool clearConnectInput)
    {
        suppressRetryActionForReturnToWaiting = true;
        helperListenerReturnToWaitingRequested = true;
        ResetToWaitingScreen(clearConnectInput);
    }

    private void ClearSessionConversationUi()
    {
        lastConversationSessionId = string.Empty;
        ChatDraft = string.Empty;
        ChatMessages.Clear();
        InboundFileTransfer = null;
        OutboundFileTransfer = null;
        OnPropertyChanged(nameof(HasChatMessages));
        OnPropertyChanged(nameof(ShowNoMessagesPlaceholder));
    }

    private void SyncConversationBoundaryFromFlow(SessionFlowSnapshot flow)
    {
        if (!flow.IsConnectedShellVisible)
        {
            return;
        }

        var sessionId = flow.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId) ||
            string.Equals(lastConversationSessionId, sessionId, StringComparison.Ordinal))
        {
            return;
        }

        ClearSessionConversationUi();
        lastConversationSessionId = sessionId;
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

    private void ShowPeerEndedNotice(string text)
    {
        peerEndedNoticeText = text;
        showPeerEndedNotice = !string.IsNullOrWhiteSpace(text);
        peerEndedNoticeTimer.Stop();
        if (showPeerEndedNotice)
        {
            peerEndedNoticeTimer.Start();
        }

        SyncTransientStatusFromRuntime();
    }

    private void ApplyTerminalPresentationFromFlow(SessionFlowSnapshot flow)
    {
        if (flow.ShouldClearConversationUi)
        {
            ClearSessionConversationUi();
            ClearRemoteScreenShareFrame();
        }

        ShowTransientBanner = false;
        TransientBannerText = string.Empty;
        CanCancelTransient = false;
        uiRecoveryTransientDismissed = flow.TerminalKind == SessionTerminalKind.PeerEnded || flow.TerminalKind == SessionTerminalKind.LocalEnded;
        IsChatInputEnabled = false;
        CanEndSession = false;
        CanSendFiles = false;
        ShowChatNotice = false;
        IsConnecting = false;

        FailureTitle = flow.FailureTitle;
        FailureMessage = flow.FailureMessage;
        FailureActionText = flow.FailureActionText;

        switch (flow.TerminalKind)
        {
            case SessionTerminalKind.LocalEnded:
                ClearPeerEndedNotice();
                StatusText = SessionFlowViewProjection.ResolveStatusText(flow, transportConfig.ApprovedStatusText);
                ConnectionState = flow.DisplayConnectionState;
                CodeInput = string.Empty;
                break;
            case SessionTerminalKind.PeerEnded:
                StatusText = SessionFlowViewProjection.ResolveStatusText(flow, transportConfig.ApprovedStatusText);
                ConnectionState = flow.DisplayConnectionState;
                CodeInput = string.Empty;
                TryShowPeerEndedNotice(flow);
                break;
            case SessionTerminalKind.Rejected:
                ClearPeerEndedNotice();
                StatusText = string.IsNullOrWhiteSpace(flow.TerminalStatusText)
                    ? UserErrorMapper.HelperRejected()
                    : flow.TerminalStatusText;
                ConnectionState = "Rejected";
                break;
            case SessionTerminalKind.Failed:
                ClearPeerEndedNotice();
                StatusText = string.IsNullOrWhiteSpace(flow.TerminalStatusText)
                    ? UserErrorMapper.HelperDisconnected()
                    : flow.TerminalStatusText;
                ConnectionState = "Failed";
                break;
            default:
                ClearPeerEndedNotice();
                break;
        }
    }

    private void TryShowPeerEndedNotice(SessionFlowSnapshot flow)
    {
        if (!flow.ShouldShowPeerEndedNotice || string.IsNullOrWhiteSpace(flow.TerminalStatusText))
        {
            ClearPeerEndedNotice();
            return;
        }

        var terminalKey = $"{flow.SessionId ?? "(none)"}|{flow.TerminalKind}|{flow.TerminalStatusText}";
        if (string.Equals(lastPeerEndedNoticeKey, terminalKey, StringComparison.Ordinal))
        {
            return;
        }

        lastPeerEndedNoticeKey = terminalKey;
        ShowPeerEndedNotice(flow.TerminalStatusText);
    }

    private void ClearPeerEndedNotice()
    {
        peerEndedNoticeTimer.Stop();
        showPeerEndedNotice = false;
        peerEndedNoticeText = string.Empty;
    }

    private void OnPeerEndedNoticeTimerTick(object? sender, EventArgs e)
    {
        peerEndedNoticeTimer.Stop();
        showPeerEndedNotice = false;
        peerEndedNoticeText = string.Empty;
        SyncTransientStatusFromRuntime();
        OnPropertyChanged(nameof(HeaderStatusText));
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

    private static string SanitizeForLog(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "(empty)"
            : value.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
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

    private bool ShouldSuppressPassiveListeningTransient(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (HasPendingHelpRequest)
        {
            return true;
        }

        if (EffectivePhase != SessionUiPhase.Waiting &&
            !string.Equals(ConnectionState, "Waiting", StringComparison.Ordinal))
        {
            return false;
        }

        return text.StartsWith("Connecting", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("Reconnecting", StringComparison.OrdinalIgnoreCase);
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
