using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using NLink.App.Configuration;
using NLink.App.Services;
using NLink.App.Services.RemoteControl;
using NLink.App.Services.ScreenCapture;
using NLink.App.Services.SessionConnect;
using NLink.App.Threading;
using NLink.Core;
using NLink.Core.Chat;
using NLink.Core.Diagnostics;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using NLink.Core.RemoteControl;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Infra.Nkn;

namespace NLink.App.ViewModels;

public sealed class HelpeePageViewModel : ViewModelBase, IDisposable, IChatPanelBindings, IWindowCloseAware
{
    private enum HelpeeConnectionViewState
    {
        Waiting,
        IncomingRequest,
        Connected,
        Disconnected,
        Failed,
    }

    private static readonly TimeSpan DefaultIncomingRequestTimeout = SessionApprovalTimeouts.DefaultHumanDecisionTimeout;
    private static readonly TimeSpan RecoveryTransientThrottle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultInviteLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan PeerEndedNoticeDuration = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan TransportScreenShareRetryDelay = TimeSpan.FromMilliseconds(300);
    private const int FileTransferUiRefreshMinIntervalMs = 125;
    private const int FileTransferUiRefreshCoalescedLogThreshold = 8;
#if DEBUG
    private static readonly TimeSpan PreviewSnapshotInterval = TimeSpan.FromSeconds(10);
#endif
    private static readonly Regex AttemptLabelRegex = new(@"\s*\(?attempt\s+\d+(?:,\s*next retry in \d+s)?\)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly bool RemoteControlDebugOverlayEnabled =
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
    private readonly IInviteTokenFactory inviteTokenFactory;
    private readonly IQrCodeService qrCodeService;
    private readonly SessionUiStateStore? uiStateStore;
    private bool hasIncomingRequest;
    private bool isRequestAllowed;
    private bool showChatNotice;
    private string connectionStatus = "Waiting for helper…";
    private string connectionState = "Waiting";
    private HelpeeConnectionViewState connectionViewState = HelpeeConnectionViewState.Waiting;
    private bool isTunaActive;
    private string tunaStatusReason = "inactive";
    private string chatDraft = string.Empty;
    private string failureTitle = string.Empty;
    private string failureMessage = string.Empty;
    private string failureActionText = string.Empty;
    private bool showTransientBanner;
    private string transientBannerText = string.Empty;
    private bool canCancelTransient;
    private bool showPeerEndedNotice;
    private string peerEndedNoticeText = string.Empty;
    private string lastPeerEndedNoticeKey = string.Empty;
    private int fileTransferUiRefreshScheduled;
    private int fileTransferUiRefreshPendingCount;
    private int fileTransferUiRefreshUrgent;
    private long lastFileTransferUiRefreshUtcMs;
    private bool hasUiRecoveryTransient;
    private string uiRecoveryTransientText = string.Empty;
    private bool uiRecoveryTransientCanCancel;
    private DateTimeOffset nextUiRecoveryBannerAllowedAt = DateTimeOffset.MinValue;
    private string uiRecoveryTransientKey = string.Empty;
    private bool uiRecoveryTransientDismissed;
    private string lastChatPanelStateLog = string.Empty;
    private long chatSendAttemptCounter;
    private SessionReliabilityAttempt? reliabilityAttempt;
    private readonly InlineTransientText copyFeedback = new();
    private string shareInviteText = string.Empty;
    private string shareInviteRawTokenText = string.Empty;
    private string shareAddressText = string.Empty;
    private string shareInviteStatusText = "Preparing invite…";
    private string automaticIdentityRecoveryWarning = string.Empty;
    private Bitmap? shareInviteQrBitmap;
    private CancellationTokenSource? shareInviteQrRefreshCts;
    private int shareInviteQrRefreshVersion;
    private string lastInviteAddressForToken = string.Empty;
    private string lastInviteHelperIdentityForToken = string.Empty;
    private DateTimeOffset shareInviteExpiresAtUtc = DateTimeOffset.MinValue;
    private string shareInviteExpiryText = string.Empty;
    private bool shareInviteAutoRefreshTriggered;
    private DateTimeOffset incomingRequestExpiresAtUtc = DateTimeOffset.MinValue;
    private string incomingRequestTimeoutText = string.Empty;
    private string inviteHelperIdentityInput = string.Empty;
    private string verifiedInviteHelperIdentity = string.Empty;
    private string verifiedInviteVerificationIdentity = string.Empty;
    private string verifiedHelpRequestTargetAddress = string.Empty;
    private bool suppressAutoApplyInviteHelperIdentityInput;
    private string incomingHelperIdentity = string.Empty;
    private readonly ObservableCollection<ScreenCaptureDisplayPickerOption> availableCaptureDisplays = new();
    private ScreenCaptureDisplayPickerOption? selectedCaptureDisplay;
    private string incomingSessionId = string.Empty;
    private CapabilityGrant incomingRequestedCapabilities;
    private string incomingApprovalSelectionKey = string.Empty;
    private bool allowIncomingChatCapability;
    private bool allowIncomingScreenShareCapability;
    private bool allowIncomingRemoteControlCapability;
    private bool allowIncomingFileTransferCapability;
    private bool allowIncomingClipboardCapability;
    private readonly DispatcherTimer shareInviteExpiryTimer;
    private readonly DispatcherTimer peerEndedNoticeTimer;
    private CancellationTokenSource? incomingRequestTimeoutCts;
    private readonly TimeSpan incomingRequestTimeout;
    private bool startupBlocked;
    private bool startupFailureBlocksAutoRestart;
    private bool autoRegeneratingAfterDisconnect;
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
    private SessionUiPhase effectivePhase;
    private bool localEndCommandInFlight;
    private bool suppressConnectedControlsAfterLocalEnd;
    private string lastAppliedPostTerminalActionKey = string.Empty;
    private SessionUiPhase lastObservedUiPhase;
    private readonly HelpeeScreenShareCoordinator screenShareCoordinator;
    private readonly bool isScreenCaptureSupported;
    private bool isScreenSharingPreviewActive;
    private Bitmap? screenSharePreviewFrame;
    private ScreenShareStatus screenSharePreviewStatus = new(ScreenShareState.Off, null, DateTimeOffset.UtcNow);
    private bool helpeePreviewSurfaceVisibleLogged;
    private bool helpeePreviewErrorVisibleLogged;
    private int screenSharePreviewStopInFlight;
    private int transportScreenShareSyncLoopActive;
    private int transportScreenShareSyncQueued;
    private bool desiredTransportScreenSharePreviewActive;
    private string desiredTransportScreenShareSyncTrigger = "init";
    private bool remoteControlConsentActionInFlight;
    private string remoteControlConsentFeedbackText = string.Empty;
    private string lastRemoteControlConsentRequestId = string.Empty;
#if DEBUG
    private string remoteControlDebugLastPointerText = "n/a";
    private string remoteControlDebugLastEventText = "n/a";
    private string remoteControlDebugUpdatedText = "n/a";
    private int remoteControlDebugEventsPerSecond;
    private int remoteControlDebugEventsInWindow;
    private long remoteControlDebugWindowStartTickMs;
#endif
    private bool remoteControlDebugPanelExpanded;
    private bool disposed;
    private int windowCloseDisconnectStarted;
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
        IScreenCaptureSourceFactory? screenCaptureSourceFactory = null,
        IInviteShareService? inviteShareService = null,
        IQrCodeService? qrCodeService = null)
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
            decodeFrame: null,
            inviteShareService: inviteShareService,
            qrCodeService: qrCodeService)
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
        Func<byte[], Bitmap>? decodeFrame,
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
        RefreshAutomaticIdentityRecoveryWarning();
        inviteTokenFactory = ConnectInputResolverFactory.CreateInviteTokenFactory();
        this.incomingRequestTimeout = incomingRequestTimeout ?? DefaultIncomingRequestTimeout;
        this.uiStateStore = uiStateStore;
        shareInviteExpiryTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        shareInviteExpiryTimer.Tick += OnShareInviteExpiryTimerTick;
        peerEndedNoticeTimer = new DispatcherTimer
        {
            Interval = PeerEndedNoticeDuration,
        };
        peerEndedNoticeTimer.Tick += OnPeerEndedNoticeTimerTick;
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

        sessionRuntime.FlowSnapshotChanged += OnFlowSnapshotChanged;
        sessionRuntime.SessionSecurityStateChanged += OnSessionSecurityStateChanged;
        sessionRuntime.TransportAccelerationStateChanged += OnTransportAccelerationStateChanged;
        sessionRuntime.TransientStatusChanged += OnTransientStatusChanged;
        sessionRuntime.IncomingJoinRequestAvailable += OnIncomingJoinRequestAvailable;
        sessionRuntime.HelpRequestDecisionAvailable += OnHelpRequestDecisionAvailable;
        sessionRuntime.Disconnected += OnRuntimeDisconnected;
        sessionRuntime.RemoteSessionEnded += OnRemoteSessionEnded;
        sessionRuntime.ScreenShareStopped += OnScreenShareStopped;
        sessionRuntime.ChatMessageReceived += OnChatMessageReceived;
        sessionRuntime.ChatMessageReceivedBeforeApproved += OnChatMessageReceivedBeforeApproved;
        sessionRuntime.ChatStateChanged += OnChatStateChanged;
        sessionRuntime.FileTransferChanged += OnFileTransferChanged;
        sessionRuntime.RemoteControlStateChanged += OnRemoteControlStateChanged;
#if DEBUG
        sessionRuntime.RemoteControlInputReceived += OnRemoteControlInputReceived;
#endif
        this.statusPresenter.StatusChanged += OnStatusPresenterChanged;
        if (this.uiStateStore is not null)
        {
            this.uiStateStore.PropertyChanged += OnUiStateStorePropertyChanged;
        }

        CopyInviteCommand = new AsyncRelayCommand(CopyInviteAsync);
        CopyAddressCommand = new AsyncRelayCommand(CopyAddressAsync);
        ShareInviteCommand = new AsyncRelayCommand(ShareInviteAsync);
        RefreshInviteCommand = new RelayCommand(RefreshInvite);
        ApplyInviteHelperIdentityCommand = new RelayCommand(ApplyInviteHelperIdentity, CanApplyInviteHelperIdentity);
        ClearInviteHelperIdentityCommand = new RelayCommand(ClearInviteHelperIdentity, CanClearInviteHelperIdentity);
        RequestHelpCommand = new AsyncRelayCommand(RequestHelpAsync, CanRequestHelp);
        AllowCommand = new RelayCommand(AllowIncomingRequest, CanAllowIncomingRequest);
        DeclineCommand = new AsyncRelayCommand(DeclineIncomingRequestAsync, CanDeclineIncomingRequest);
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
        RetryCommand = new AsyncRelayCommand(RetryAsync);
        CancelTransientCommand = new AsyncRelayCommand(CancelTransientAsync, CanCancelTransientOperation);
        OpenDiagnosticsCommand = new RelayCommand(OpenDiagnostics, CanOpenDiagnosticsCommand);
        CancelCommand = new RelayCommand(CancelAndGoBack);
        EndSessionCommand = new RelayCommand(EndSession, CanTriggerEndSession);
        ToggleScreenSharePreviewCommand = new RelayCommand(ToggleScreenSharePreview, CanToggleScreenSharePreview);
        RequestControlCommand = new RelayCommand(RequestRemoteControl, CanRequestRemoteControlAction);
        StopControlCommand = new RelayCommand(StopRemoteControl, CanStopRemoteControlAction);
        RestartAsAdministratorCommand = new RelayCommand(RestartAsAdministrator, CanRestartAsAdministratorAction);
        ToggleControlModeCommand = new RelayCommand(static () => { }, static () => false);
        ToggleRemoteControlDebugPanelCommand = new RelayCommand(ToggleRemoteControlDebugPanel, CanToggleRemoteControlDebugPanel);
        AllowControlConsentCommand = new AsyncRelayCommand(AllowControlConsentAsync, CanRespondToControlConsent);
        DenyControlConsentCommand = new AsyncRelayCommand(DenyControlConsentAsync, CanRespondToControlConsent);

        InitializeStartupAvailabilityState();
        presenterBannerStatus = NormalizeStatusForDisplay(this.statusPresenter.CurrentStatus);
        BannerStatus = presenterBannerStatus;
        if (!IsStartupBlocked)
        {
            StartHosting();
        }
        EnsureInviteSnapshot(forceNewToken: false);
        shareInviteExpiryTimer.Start();
        SyncTransientStatusFromRuntime();
        if (this.uiStateStore is not null && this.uiStateStore.Phase == SessionUiPhase.Idle)
        {
            this.uiStateStore.SetPhase(
                IsStartupBlocked ? SessionUiPhase.Idle : SessionUiPhase.Waiting,
                "Constructor:HelpeeSeed");
        }
        InitializeCaptureTargetSelection();
        ApplySessionBannerPolicy();
        UpdateUiFromSnapshot();
        SyncTunaActiveFromRuntime();
    }

    public string ShareInvite => shareInviteText;
    public string ShareInviteRawToken => shareInviteRawTokenText;
    public string ShareAddress => shareAddressText;
    public string ShareInviteStatusText => shareInviteStatusText;
    public Bitmap? ShareInviteQrImage => shareInviteQrBitmap;
    public bool ShowShareInviteQr => ShareInviteQrImage is not null;
    public bool ShowShareInviteQrPlaceholder => !ShowShareInviteQr;
    public bool ShowShareInviteStatus => (!HasShareInvite || HasAutomaticIdentityRecoveryNotice) && !string.IsNullOrWhiteSpace(ShareInviteStatusText);
    public string ShareInviteExpiryText => shareInviteExpiryText;
    public bool ShowShareInviteExpiry => HasShareInvite && !string.IsNullOrWhiteSpace(ShareInviteExpiryText);
    public string IncomingRequestTimeoutText => incomingRequestTimeoutText;
    public bool ShowIncomingRequestTimeout =>
        ShowIncomingRequestPanel &&
        HasIncomingRequest &&
        !string.IsNullOrWhiteSpace(IncomingRequestTimeoutText);
    public bool HasShareInvite => !string.IsNullOrWhiteSpace(ShareInvite);
    private bool HasAutomaticIdentityRecoveryNotice =>
        !string.IsNullOrWhiteSpace(automaticIdentityRecoveryWarning) &&
        shareInviteStatusText.Contains(automaticIdentityRecoveryWarning, StringComparison.Ordinal);
    public bool HasShareInviteRawToken =>
        !string.IsNullOrWhiteSpace(ShareInviteRawToken) &&
        !string.Equals(ShareInviteRawToken, ShareInvite, StringComparison.Ordinal);
    public bool HasShareAddress => !string.IsNullOrWhiteSpace(ShareAddress);
    public bool ShowInviteHelperIdentityPanel => ShowWaitingPanel && !IsUnboundPublicInviteFlowAvailable;
    public bool ShowHeaderCaptureDisplayPicker =>
        ShowConnectedPanel &&
        CanShowScreenShareAction &&
        isScreenCaptureSupported &&
        FeatureFlags.EnableScreenShareCapture;

    public ObservableCollection<ScreenCaptureDisplayPickerOption> AvailableCaptureDisplays => availableCaptureDisplays;

    public ScreenCaptureDisplayPickerOption? SelectedCaptureDisplay
    {
        get => selectedCaptureDisplay;
        set
        {
            SetProperty(ref selectedCaptureDisplay, value);
        }
    }
    public bool HasVerifiedInviteHelperIdentity => !string.IsNullOrWhiteSpace(verifiedInviteHelperIdentity);

    public string InviteHelperIdentityInput
    {
        get => inviteHelperIdentityInput;
        set
        {
            value ??= string.Empty;
            if (SetProperty(ref inviteHelperIdentityInput, value))
            {
                if (!suppressAutoApplyInviteHelperIdentityInput)
                {
                    AutoApplyInviteHelperIdentityIfPossible();
                }

                OnPropertyChanged(nameof(InviteHelperIdentityStatusText));
                OnPropertyChanged(nameof(CanApplyInviteHelperIdentityAction));
                OnPropertyChanged(nameof(CanClearInviteHelperIdentityAction));
                OnPropertyChanged(nameof(CanRequestHelpAction));
                ApplyInviteHelperIdentityCommand.NotifyCanExecuteChanged();
                ClearInviteHelperIdentityCommand.NotifyCanExecuteChanged();
                RequestHelpCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string InviteHelperIdentityStatusText
    {
        get
        {
            if (!ShowInviteHelperIdentityPanel)
            {
                return string.Empty;
            }

            var normalizedInput = InviteHelperIdentityInput.Trim();
            if (HasVerifiedInviteHelperIdentity &&
                TryResolveInviteHelperIdentityInput(out var resolvedHelperIdentity, out _, out _, out _) &&
                string.Equals(resolvedHelperIdentity.Value, verifiedInviteHelperIdentity, StringComparison.Ordinal))
            {
                return "Invite will only work for this helper.";
            }

            if (string.IsNullOrWhiteSpace(normalizedInput))
            {
                return HasVerifiedInviteHelperIdentity
                    ? "Paste a different helper address to refresh the invite binding."
                    : "Paste or import the helper address your helper shared with you.";
            }

            if (!TryResolveInviteHelperIdentityInput(out _, out _, out _, out _))
            {
                return "Enter a valid helper address.";
            }

            return HasVerifiedInviteHelperIdentity
                ? "Use this helper address to refresh the invite."
                : "Use this helper address to generate a bound invite.";
        }
    }

    public bool ShowVerifiedInviteHelperIdentity => ShowInviteHelperIdentityPanel && HasVerifiedInviteHelperIdentity;
    public string VerifiedInviteHelperIdentityText =>
        PeerAddress.TryParse(verifiedInviteHelperIdentity, out var helperIdentity)
            ? helperIdentity.Value
            : string.Empty;
    public string VerifiedInviteHelperVerificationCode =>
        HelperVerificationCodeFormatter.FormatOrNull(verifiedInviteVerificationIdentity) ?? string.Empty;
    public bool HasVerifiedInviteHelperVerificationCode => !string.IsNullOrWhiteSpace(VerifiedInviteHelperVerificationCode);
    public string HeaderVerificationCodeText =>
        ShowIncomingRequestPanel && HasIncomingHelperVerificationCode
            ? IncomingHelperVerificationCode
            : ShowVerifiedInviteHelperIdentity && HasVerifiedInviteHelperVerificationCode
                ? VerifiedInviteHelperVerificationCode
                : string.Empty;
    public bool ShowHeaderVerificationCode =>
        !ShowInviteHelperIdentityPanel &&
        !string.IsNullOrWhiteSpace(HeaderVerificationCodeText);
    public string FirstPillVerificationCodeText =>
        ShowInviteHelperIdentityPanel
            ? HeaderVerificationCodeText
            : string.Empty;
    public bool ShowFirstPillVerificationCode =>
        ShowInviteHelperIdentityPanel &&
        !string.IsNullOrWhiteSpace(FirstPillVerificationCodeText);
    public string VerifiedInviteTechnicalHelperIdentityText => verifiedInviteHelperIdentity;
    public bool HasVerifiedInviteTechnicalHelperIdentity => !string.IsNullOrWhiteSpace(VerifiedInviteTechnicalHelperIdentityText);
    public bool CanApplyInviteHelperIdentityAction => CanApplyInviteHelperIdentity();
    public bool CanClearInviteHelperIdentityAction => CanClearInviteHelperIdentity();

    public string IncomingHelperName =>
        string.IsNullOrWhiteSpace(incomingHelperIdentity)
            ? "Verified helper"
            : incomingHelperIdentity;

    public string IncomingHelperIdentityText =>
        string.IsNullOrWhiteSpace(incomingHelperIdentity)
            ? "Waiting for verified helper address."
            : incomingHelperIdentity;

    public string IncomingHelperVerificationCode =>
        HelperVerificationCodeFormatter.FormatOrNull(incomingHelperIdentity) ?? string.Empty;

    public bool HasIncomingHelperVerificationCode => !string.IsNullOrWhiteSpace(IncomingHelperVerificationCode);

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
        ShowIncomingRequestPanel &&
        HasIncomingRequest &&
        HasSessionVerificationCode;

    public string IncomingTechnicalHelperIdentityText => IncomingHelperIdentityText;

    public bool HasIncomingTechnicalHelperIdentity => !string.IsNullOrWhiteSpace(IncomingTechnicalHelperIdentityText);

    public string IncomingSessionIdText =>
        string.IsNullOrWhiteSpace(incomingSessionId)
            ? string.Empty
            : $"Session {incomingSessionId}";

    public string IncomingTechnicalSessionIdText => IncomingSessionIdText;

    public bool HasIncomingTechnicalSessionId => !string.IsNullOrWhiteSpace(IncomingTechnicalSessionIdText);

    public bool HasIncomingTechnicalDetails =>
        !string.IsNullOrWhiteSpace(IncomingTechnicalHelperIdentityText) ||
        !string.IsNullOrWhiteSpace(IncomingTechnicalSessionIdText);

    public string IncomingRequestedCapabilitiesText =>
        BuildCapabilitySummary(incomingRequestedCapabilities);

    public string IncomingApprovedCapabilitiesText =>
        BuildCapabilitySummary(GetSelectedIncomingApprovalCapabilities());

    public bool ShowIncomingRequestedCapabilities => incomingRequestedCapabilities != CapabilityGrant.None;
    public bool ShowIncomingChatCapability => (incomingRequestedCapabilities & CapabilityGrant.Chat) == CapabilityGrant.Chat;
    public bool ShowIncomingScreenShareCapability => (incomingRequestedCapabilities & CapabilityGrant.ScreenShare) == CapabilityGrant.ScreenShare;
    public bool ShowIncomingRemoteControlCapability => (incomingRequestedCapabilities & CapabilityGrant.RemoteControl) == CapabilityGrant.RemoteControl;
    public bool CanAllowIncomingRemoteControlCapability => ShowIncomingRemoteControlCapability && allowIncomingScreenShareCapability;
    public bool ShowIncomingFileTransferCapability => (incomingRequestedCapabilities & CapabilityGrant.FileTransfer) == CapabilityGrant.FileTransfer;
    public bool ShowIncomingClipboardCapability => (incomingRequestedCapabilities & CapabilityGrant.Clipboard) == CapabilityGrant.Clipboard;

    public bool AllowIncomingChatCapability
    {
        get => allowIncomingChatCapability;
        set => SetIncomingCapabilitySelection(ref allowIncomingChatCapability, value, nameof(AllowIncomingChatCapability));
    }

    public bool AllowIncomingScreenShareCapability
    {
        get => allowIncomingScreenShareCapability;
        set => SetIncomingCapabilitySelection(ref allowIncomingScreenShareCapability, value, nameof(AllowIncomingScreenShareCapability));
    }

    public bool AllowIncomingRemoteControlCapability
    {
        get => allowIncomingRemoteControlCapability;
        set => SetIncomingCapabilitySelection(ref allowIncomingRemoteControlCapability, value, nameof(AllowIncomingRemoteControlCapability));
    }

    public bool AllowIncomingFileTransferCapability
    {
        get => allowIncomingFileTransferCapability;
        set => SetIncomingCapabilitySelection(ref allowIncomingFileTransferCapability, value, nameof(AllowIncomingFileTransferCapability));
    }

    public bool AllowIncomingClipboardCapability
    {
        get => allowIncomingClipboardCapability;
        set => SetIncomingCapabilitySelection(ref allowIncomingClipboardCapability, value, nameof(AllowIncomingClipboardCapability));
    }

    public bool CanAllowIncomingRequestAction => CanAllowIncomingRequest();

    public InlineTransientText CopyFeedback => copyFeedback;
    public bool ShowCopyFeedbackInline => ShowWaitingPanel && copyFeedback.IsVisible;

    public bool HasIncomingRequest
    {
        get => hasIncomingRequest;
        private set
        {
            if (SetProperty(ref hasIncomingRequest, value))
            {
                OnPropertyChanged(nameof(CanAllowIncomingRequestAction));
                OnPropertyChanged(nameof(ShowIncomingRequestTimeout));
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
                OnPropertyChanged(nameof(CanAllowIncomingRequestAction));
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
                OnPropertyChanged(nameof(ShowTransientStatusPanel));
            }
        }
    }

    public string ConnectionState
    {
        get => connectionState;
        private set
        {
            value ??= string.Empty;
            var nextViewState = MapConnectionViewState(value);
            var viewStateChanged = connectionViewState != nextViewState;
            if (SetProperty(ref connectionState, value) || viewStateChanged)
            {
                connectionViewState = nextViewState;
                OnPropertyChanged(nameof(IsWaitingView));
                OnPropertyChanged(nameof(IsIncomingRequestView));
                OnPropertyChanged(nameof(IsConnectedView));
                OnPropertyChanged(nameof(ShowWaitingPanel));
                OnPropertyChanged(nameof(ShowIncomingRequestPanel));
                OnPropertyChanged(nameof(ShowConnectedPanel));
                OnPropertyChanged(nameof(ShowHeaderCaptureDisplayPicker));
                OnPropertyChanged(nameof(ShowStartupBlockedPanel));
                OnPropertyChanged(nameof(ShowWaitingStatusLine));
                OnPropertyChanged(nameof(ShowWaitingInviteActions));
                OnPropertyChanged(nameof(ShowInviteHelperIdentityPanel));
                OnPropertyChanged(nameof(ShowVerifiedInviteHelperIdentity));
                OnPropertyChanged(nameof(InviteHelperIdentityStatusText));
                OnPropertyChanged(nameof(HeaderVerificationCodeText));
                OnPropertyChanged(nameof(ShowHeaderVerificationCode));
                OnPropertyChanged(nameof(FirstPillVerificationCodeText));
                OnPropertyChanged(nameof(ShowFirstPillVerificationCode));
                NotifySessionVerificationPropertiesChanged();
                OnPropertyChanged(nameof(ShowBackButton));
                OnPropertyChanged(nameof(StatusLineText));
                OnPropertyChanged(nameof(SecondaryActionText));
                OnPropertyChanged(nameof(ShowChatSection));
                OnPropertyChanged(nameof(ShowFailurePanel));
                OnPropertyChanged(nameof(HeaderStatusText));
                OnPropertyChanged(nameof(ShowTransientStatusPanel));
                OnPropertyChanged(nameof(ShowIncomingRequestTimeout));
                OnPropertyChanged(nameof(ShowStopControlAction));
                OnPropertyChanged(nameof(CanStopControl));
                OnPropertyChanged(nameof(ShowRemoteControlActiveStatus));
                OnPropertyChanged(nameof(ShowRemoteControlPreviewActiveCue));
                NotifyRemoteControlDiagnosticsChanged();
                NotifyRemoteControlConsentUiChanged();
                StopControlCommand.NotifyCanExecuteChanged();
                ApplySessionBannerPolicy();
            }
        }
    }

    public bool IsWaitingView => connectionViewState is HelpeeConnectionViewState.Waiting or HelpeeConnectionViewState.Disconnected or HelpeeConnectionViewState.Failed;

    public bool IsIncomingRequestView => connectionViewState == HelpeeConnectionViewState.IncomingRequest;

    public bool IsConnectedView => connectionViewState == HelpeeConnectionViewState.Connected;

    public bool ShowChatSection => IsConnectedView;
    public bool ShowWaitingPanel => IsWaitingView && !IsStartupBlocked;
    public bool ShowIncomingRequestPanel => IsIncomingRequestView && !IsStartupBlocked;
    public bool ShowConnectedPanel => ShowChatSection && !IsStartupBlocked;
    public bool ShowFailurePanel => (!string.IsNullOrWhiteSpace(FailureTitle) || !string.IsNullOrWhiteSpace(FailureMessage)) &&
                                    (IsStartupBlocked || connectionViewState == HelpeeConnectionViewState.Failed);
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

    public string SecondaryActionText => IsConnectedView ? "Disconnect" : "Refresh invite";

    public string HeaderStatusText => BuildHeaderStatusText();

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
                OnPropertyChanged(nameof(ShowHeaderCaptureDisplayPicker));
                OnPropertyChanged(nameof(ShowStartupBlockedPanel));
                OnPropertyChanged(nameof(ShowWaitingStatusLine));
                OnPropertyChanged(nameof(ShowWaitingInviteActions));
                OnPropertyChanged(nameof(ShowInviteHelperIdentityPanel));
                OnPropertyChanged(nameof(ShowVerifiedInviteHelperIdentity));
                OnPropertyChanged(nameof(InviteHelperIdentityStatusText));
                OnPropertyChanged(nameof(ShowBackButton));
                OnPropertyChanged(nameof(ShowRetryAction));
                OnPropertyChanged(nameof(ShowOpenDiagnosticsLink));
                OnPropertyChanged(nameof(ShowFailurePanel));
                OnPropertyChanged(nameof(ShowIncomingRequestTimeout));
                OpenDiagnosticsCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool ShowHostingUi => !IsStartupBlocked;
    public bool ShowWaitingInviteActions =>
        ShowHostingUi &&
        connectionViewState == HelpeeConnectionViewState.Waiting &&
        HasShareInvite &&
        (IsUnboundPublicInviteFlowAvailable || HasVerifiedInviteHelperIdentity);

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

    public bool ShowChatTopBar => !FeatureFlags.EnableSessionHeader;

    public SessionUiPhase EffectivePhase
    {
        get => effectivePhase;
        private set
        {
            if (SetProperty(ref effectivePhase, value))
            {
                OnPropertyChanged(nameof(HeaderStatusText));
                OnPropertyChanged(nameof(ShowTransientStatusPanel));
                OnPropertyChanged(nameof(ShowStopControlAction));
                OnPropertyChanged(nameof(CanStopControl));
                OnPropertyChanged(nameof(ShowRemoteControlActiveStatus));
                OnPropertyChanged(nameof(ShowRemoteControlAdminWarning));
                OnPropertyChanged(nameof(CanRestartAsAdministrator));
                OnPropertyChanged(nameof(ShowRemoteControlPreviewActiveCue));
                OnPropertyChanged(nameof(ShowRemoteControlDebugToggle));
                OnPropertyChanged(nameof(ShowRemoteControlDebugOverlay));
                NotifyRemoteControlConsentUiChanged();
            }
        }
    }

    public bool IsChatReady => sessionRuntime.CanSendChat;

    private bool IsRemoteControlUiConnected =>
        SessionFlowViewProjection.IsConnectedShell(sessionRuntime.FlowSnapshot);

    public bool CanStartOrConnect
    {
        get => canStartOrConnect;
        private set
        {
            if (SetProperty(ref canStartOrConnect, value))
            {
                OnPropertyChanged(nameof(CanStartConnect));
                OnPropertyChanged(nameof(CanRequestHelpAction));
                OnPropertyChanged(nameof(CanApplyInviteHelperIdentityAction));
                OnPropertyChanged(nameof(CanClearInviteHelperIdentityAction));
                ApplyInviteHelperIdentityCommand.NotifyCanExecuteChanged();
                ClearInviteHelperIdentityCommand.NotifyCanExecuteChanged();
                RequestHelpCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool CanStartConnect => CanStartOrConnect;

    public bool CanRequestHelpAction => CanRequestHelp();

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

    public bool SessionSupportsRemoteControl => sessionRuntime.SessionSupportsRemoteControl;
    public bool ShowRequestControlAction => false;
    public bool CanRequestControl => false;
    public bool ShowStopControlAction => IsRemoteControlUiConnected && sessionRuntime.ControlState == ControlState.Active;
    public string StopControlButtonText => "Stop control";
    public bool CanStopControl => IsRemoteControlUiConnected && sessionRuntime.ControlState == ControlState.Active;
    public bool ShowRemoteControlActiveStatus => IsRemoteControlUiConnected && sessionRuntime.ControlState == ControlState.Active;
    public bool ShowRemoteControlAdminWarning => IsRemoteControlUiConnected && sessionRuntime.RemoteControlAdminRestartRequired;
    public string RemoteControlAdminWarningText => sessionRuntime.RemoteControlAdminWarningText;
    public bool CanRestartAsAdministrator =>
        ShowRemoteControlAdminWarning &&
        OperatingSystem.IsWindows() &&
        !sessionRuntime.RemoteControlProcessElevated;
    public bool ShowRemoteControlPreviewActiveCue => IsRemoteControlUiConnected && ShowScreenSharePreviewFrame && sessionRuntime.ControlState == ControlState.Active;
    public bool ShowControlModeToggle => false;
    public bool CanControlModeToggle => false;
    public string ControlModeButtonText => "Control mode: Off";
    public bool ShowRemoteControlDebugToggle =>
        RemoteControlDebugOverlayEnabled &&
        IsRemoteControlUiConnected &&
        ShowScreenSharePreviewFrame;
    public bool ShowRemoteControlDebugOverlay =>
        ShowRemoteControlDebugToggle &&
        remoteControlDebugPanelExpanded;
    public string RemoteControlDebugToggleText => ShowRemoteControlDebugOverlay ? "Hide diagnostics" : "Show diagnostics";
    public string RemoteControlDiagnosticsRoleText => "Helpee";
    public string RemoteControlDiagnosticsControlStateText => sessionRuntime.ControlState.ToString();
    public string RemoteControlDiagnosticsControlModeText => "n/a";
    public string RemoteControlDiagnosticsDisplayText =>
        sessionRuntime.RemoteControlMappingDisplayId is { Length: > 0 } displayId
            ? $"{displayId}@{sessionRuntime.RemoteControlMappingRevision?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}"
            : "n/a";
    public string RemoteControlDiagnosticsCaptureFrameText => FormatCaptureFrameText(GetRemoteControlDiagnosticsSnapshot());
    public string RemoteControlDiagnosticsMoveStatsText => FormatMoveStatsText(GetRemoteControlDiagnosticsSnapshot());
    public string RemoteControlDiagnosticsSuppressionsText => FormatSuppressionText(GetRemoteControlDiagnosticsSnapshot());
    public string RemoteControlDiagnosticsLastMappedText => FormatLastMappedText(GetRemoteControlDiagnosticsSnapshot());
    public string RemoteControlDebugPointerText =>
#if DEBUG
        remoteControlDebugLastPointerText;
#else
        "n/a";
#endif
    public string RemoteControlDebugEventText =>
#if DEBUG
        remoteControlDebugLastEventText;
#else
        "n/a";
#endif
    public string RemoteControlDebugRequestIdText => sessionRuntime.CurrentControlRequestId ?? "n/a";
    public string RemoteControlDebugControllerPeerText => sessionRuntime.ControllerPeerId ?? "n/a";
    public string RemoteControlDebugDisplayText => RemoteControlDiagnosticsDisplayText;
    public string RemoteControlDebugInjectorText => sessionRuntime.RemoteControlInjectionSupported ? "supported" : "unsupported";
    public string RemoteControlDebugQueueText =>
        $"inj={sessionRuntime.RemoteControlInjectionQueueDepth}; move={sessionRuntime.RemoteControlOutgoingMouseMoveQueueDepth}";
    public string RemoteControlDebugGuardrailCountersText =>
        $"clamps={sessionRuntime.RemoteControlDebugMappingClampCount}; drops={sessionRuntime.RemoteControlDebugQueueDropCount}; suppressed={sessionRuntime.RemoteControlDebugInjectionSuppressedCount}; flushes={sessionRuntime.RemoteControlDebugQueueFlushCount}";
#if DEBUG
    internal RemoteControlDebugSnapshot RemoteControlDiagnosticsSnapshotForDebug =>
        RemoteControlDebugDiagnostics.Snapshot(RemoteControlDiagnosticsRole.Helpee);
#endif
    public string RemoteControlDebugStatsText =>
#if DEBUG
        $"{remoteControlDebugEventsPerSecond} eps";
#else
        "0 eps";
#endif
    public string RemoteControlDebugUpdatedText =>
#if DEBUG
        remoteControlDebugUpdatedText;
#else
        "n/a";
#endif
    public bool ShowRemoteControlConsentDialog =>
        IsRemoteControlUiConnected &&
        IsScreenSharingPreviewActive &&
        sessionRuntime.HasPendingRemoteControlConsentPrompt;
    public string RemoteControlConsentTitle => "Allow remote control?";
    public string RemoteControlConsentMessage => "The helper is requesting control of your mouse and keyboard.";
    public string RemoteControlConsentFeedbackText => remoteControlConsentFeedbackText;
    public bool ShowRemoteControlConsentFeedback =>
        ShowRemoteControlConsentDialog &&
        !string.IsNullOrWhiteSpace(RemoteControlConsentFeedbackText);
    public bool CanSubmitRemoteControlConsent => CanRespondToControlConsent();

    public bool IsScreenSharingPreviewActive
    {
        get => isScreenSharingPreviewActive;
        private set
        {
            if (SetProperty(ref isScreenSharingPreviewActive, value))
            {
                OnPropertyChanged(nameof(HeaderStatusText));
                OnPropertyChanged(nameof(ShowTransientStatusPanel));
                NotifyRemoteControlConsentUiChanged();
                OnPropertyChanged(nameof(ShowRemoteControlActiveStatus));
                ToggleScreenSharePreviewCommand.NotifyCanExecuteChanged();
#if DEBUG
                UpdatePreviewSnapshotTimer();
#endif
                RequestTransportScreenShareSync(value, "preview_active_changed");
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
                    OnPropertyChanged(nameof(ShowRemoteControlPreviewActiveCue));
                    NotifyRemoteControlDiagnosticsChanged();
                    if (ShowScreenSharePreviewFrame && !helpeePreviewSurfaceVisibleLogged)
                    {
                        LocalOperationalLog.Info(
                            "HelpeeUi",
                            $"event=helpee_screenshare_preview_surface_visible; role=helpee_preview; header_status={SanitizeForLog(HeaderStatusText)}; preview_status={ScreenSharePreviewStatus.State}");
                        helpeePreviewSurfaceVisibleLogged = true;
                    }
                    else if (!ShowScreenSharePreviewFrame)
                    {
                        helpeePreviewSurfaceVisibleLogged = false;
                    }
                }

                if (ShowScreenSharePreviewFrame && !helpeePreviewSurfaceVisibleLogged)
                {
                    LocalOperationalLog.Info(
                        "HelpeeUi",
                        $"event=helpee_screenshare_preview_surface_visible; role=helpee_preview; header_status={SanitizeForLog(HeaderStatusText)}; preview_status={ScreenSharePreviewStatus.State}");
                    helpeePreviewSurfaceVisibleLogged = true;
                }
                else if (!ShowScreenSharePreviewFrame)
                {
                    helpeePreviewSurfaceVisibleLogged = false;
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
                    OnPropertyChanged(nameof(ShowRemoteControlPreviewActiveCue));
                    NotifyRemoteControlDiagnosticsChanged();
                }

                if (previousShowDefaultPlaceholder != ShowDefaultScreenSharePlaceholder)
                {
                    OnPropertyChanged(nameof(ShowDefaultScreenSharePlaceholder));
                }

                if (previousShowViewerError != ShowScreenShareViewerError)
                {
                    OnPropertyChanged(nameof(ShowScreenShareViewerError));
                    if (ShowScreenShareViewerError && !helpeePreviewErrorVisibleLogged)
                    {
                        LocalOperationalLog.Info(
                            "HelpeeUi",
                            $"event=helpee_screenshare_preview_error_visible; role=helpee_preview; header_status={SanitizeForLog(HeaderStatusText)}; message={SanitizeForLog(ScreenShareViewerMessage)}");
                        helpeePreviewErrorVisibleLogged = true;
                    }
                    else
                    {
                        helpeePreviewErrorVisibleLogged = false;
                    }
                }

                if (!string.Equals(previousViewerMessage, ScreenShareViewerMessage, StringComparison.Ordinal))
                {
                    OnPropertyChanged(nameof(ScreenShareViewerMessage));
                }

                if (!string.Equals(previousHeaderStatusText, HeaderStatusText, StringComparison.Ordinal))
                {
                    OnPropertyChanged(nameof(HeaderStatusText));
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

    public bool ShowSendFileAction =>
        EffectivePhase == SessionUiPhase.Connected &&
        sessionRuntime.CanPerform(SessionCapability.FileTransfer);

    public bool CanSendFileAction => CanSendFiles;

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

    public bool ShowChatNotice
    {
        get => showChatNotice;
        private set => SetProperty(ref showChatNotice, value);
    }

    public string ChatNoticeText => "You received a message";

    public IAsyncRelayCommand CopyInviteCommand { get; }
    public IAsyncRelayCommand CopyAddressCommand { get; }
    public IAsyncRelayCommand ShareInviteCommand { get; }
    public IRelayCommand RefreshInviteCommand { get; }
    public IRelayCommand ApplyInviteHelperIdentityCommand { get; }
    public IRelayCommand ClearInviteHelperIdentityCommand { get; }
    public IAsyncRelayCommand RequestHelpCommand { get; }

    public RelayCommand AllowCommand { get; }

    public IAsyncRelayCommand DeclineCommand { get; }

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
    public IRelayCommand ToggleScreenSharePreviewCommand { get; }
    public IRelayCommand RequestControlCommand { get; }
    public IRelayCommand StopControlCommand { get; }
    public IRelayCommand RestartAsAdministratorCommand { get; }
    public IRelayCommand ToggleControlModeCommand { get; }
    public IRelayCommand ToggleRemoteControlDebugPanelCommand { get; }
    public IAsyncRelayCommand AllowControlConsentCommand { get; }
    public IAsyncRelayCommand DenyControlConsentCommand { get; }
    public IRelayCommand StatusBannerCopyDiagnosticsCommand => OpenDiagnosticsCommand;
    public IAsyncRelayCommand StatusBannerCancelCommand => CancelTransientCommand;
    public event EventHandler? SendFileRequested;

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

    public bool ShowRetryAction => !IsStartupBlocked && sessionRuntime.FlowSnapshot.ShowRetryAction;
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

        shareInviteExpiryTimer.Stop();
        shareInviteExpiryTimer.Tick -= OnShareInviteExpiryTimerTick;
        peerEndedNoticeTimer.Stop();
        peerEndedNoticeTimer.Tick -= OnPeerEndedNoticeTimerTick;
        shareInviteQrRefreshCts?.Cancel();
        shareInviteQrRefreshCts?.Dispose();
        shareInviteQrRefreshCts = null;
        shareInviteQrBitmap?.Dispose();
        shareInviteQrBitmap = null;

        sessionRuntime.FlowSnapshotChanged -= OnFlowSnapshotChanged;
        sessionRuntime.SessionSecurityStateChanged -= OnSessionSecurityStateChanged;
        sessionRuntime.TransportAccelerationStateChanged -= OnTransportAccelerationStateChanged;
        sessionRuntime.TransientStatusChanged -= OnTransientStatusChanged;
        sessionRuntime.IncomingJoinRequestAvailable -= OnIncomingJoinRequestAvailable;
        sessionRuntime.HelpRequestDecisionAvailable -= OnHelpRequestDecisionAvailable;
        sessionRuntime.Disconnected -= OnRuntimeDisconnected;
        sessionRuntime.RemoteSessionEnded -= OnRemoteSessionEnded;
        sessionRuntime.ScreenShareStopped -= OnScreenShareStopped;
        sessionRuntime.ChatMessageReceived -= OnChatMessageReceived;
        sessionRuntime.ChatMessageReceivedBeforeApproved -= OnChatMessageReceivedBeforeApproved;
        sessionRuntime.ChatStateChanged -= OnChatStateChanged;
        sessionRuntime.FileTransferChanged -= OnFileTransferChanged;
        sessionRuntime.RemoteControlStateChanged -= OnRemoteControlStateChanged;
#if DEBUG
        sessionRuntime.RemoteControlInputReceived -= OnRemoteControlInputReceived;
#endif
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
        ForceCloseWindowsGraphicsCaptureLeases("helpee_viewmodel_dispose");
        copyFeedback.Dispose();
        CancelIncomingRequestTimeout();
        RunSynchronousCleanup(() => sessionRuntime.ResetAsync());
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

    private void RestartWaitingSession(
        bool preserveHelperIdentityForRetry,
        bool preservePeerEndedNotice,
        string? preservedPeerEndedText = null)
    {
        if (ShouldKeepVisibleTerminalFailureBeforeAutoRestart())
        {
            AppLog.Info("Helpee waiting-session restart blocked; reason=visible_terminal_failure");
            ApplyTerminalPresentationFromFlow(sessionRuntime.FlowSnapshot);
            return;
        }

        autoRegeneratingAfterDisconnect = false;
        localEndCommandInFlight = false;
        lastAppliedPostTerminalActionKey = string.Empty;

        HasIncomingRequest = false;
        IsRequestAllowed = false;
        ShowChatNotice = false;
        if (!preserveHelperIdentityForRetry)
        {
            InviteHelperIdentityInput = string.Empty;
            SetVerifiedInviteHelperIdentity(null, refreshInvite: false);
        }
        ClearSessionConversationUi();
        ConnectionStatus = "Waiting for helper…";
        ConnectionState = "Waiting";

        StartHosting();
        EnsureInviteSnapshot(forceNewToken: true);
        if (preservePeerEndedNotice)
        {
            peerEndedNoticeText = string.IsNullOrWhiteSpace(preservedPeerEndedText)
                ? "The other side ended the session."
                : preservedPeerEndedText;
            showPeerEndedNotice = true;
            SyncTransientStatusFromRuntime();
        }
        else
        {
            SyncTransientStatusFromRuntime();
        }
    }

    private void ToggleScreenSharePreview()
    {
        if (!IsScreenSharingPreviewActive)
        {
            PersistSelectedCaptureTargetForShareStart();
        }

        screenShareCoordinator.Toggle();
    }

    private bool CanToggleScreenSharePreview()
    {
        if (disposed || ScreenSharePreviewStatus.State == ScreenShareState.Starting)
        {
            return false;
        }

        return IsScreenSharingPreviewActive ||
               (CanShowScreenShareAction && sessionRuntime.CanPerform(SessionCapability.ScreenShare));
    }

    private void InitializeCaptureTargetSelection()
    {
        RefreshCaptureDisplayOptions();
        var persisted = ScreenCaptureTargetStore.Load();

        SelectedCaptureDisplay = persisted.Mode == ScreenCaptureTargetMode.Display && persisted.HasDisplayId
            ? availableCaptureDisplays.FirstOrDefault(
                display => string.Equals(display.DisplayId, persisted.DisplayId, StringComparison.OrdinalIgnoreCase))
            : availableCaptureDisplays.FirstOrDefault();
    }

    private void RefreshCaptureDisplayOptions()
    {
        availableCaptureDisplays.Clear();
        availableCaptureDisplays.Add(new ScreenCaptureDisplayPickerOption(null, "Primary display"));
        if (OperatingSystem.IsWindows())
        {
            foreach (var display in WindowsScreenCaptureTargetCatalog.GetDisplays())
            {
                availableCaptureDisplays.Add(new ScreenCaptureDisplayPickerOption(display.Id, display.Label));
            }
        }

        if (selectedCaptureDisplay is null ||
            !availableCaptureDisplays.Any(option =>
                string.Equals(option.DisplayId, selectedCaptureDisplay.DisplayId, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedCaptureDisplay = availableCaptureDisplays.FirstOrDefault();
        }
    }

    private void PersistSelectedCaptureTargetForShareStart()
    {
        RefreshCaptureDisplayOptions();
        var selection = BuildSelectedCaptureTargetSelection();
        ScreenCaptureTargetStore.Save(selection);
    }

    private ScreenCaptureTargetSelection BuildSelectedCaptureTargetSelection()
    {
        return string.IsNullOrWhiteSpace(SelectedCaptureDisplay?.DisplayId)
            ? ScreenCaptureTargetSelection.PrimaryDisplay
            : new ScreenCaptureTargetSelection(
                ScreenCaptureTargetMode.Display,
                SelectedCaptureDisplay.DisplayId,
                null,
                default);
    }

    private async Task RetryAsync()
    {
        ClearPeerEndedNotice();
        startupFailureBlocksAutoRestart = false;
        PrepareForNewSession();

        CancelIncomingRequestTimeout();

        await sessionRuntime.ResetAsync();

        await UiThreadDispatch.RunAsync(() =>
        {
            HasIncomingRequest = false;
            IsRequestAllowed = false;
            ShowChatNotice = false;
            ConnectionStatus = "Waiting for helper…";
            ConnectionState = "Waiting";
        });

        StartHosting(allowAfterVisibleTerminalFailure: true);
        EnsureInviteSnapshot(forceNewToken: true);
    }

    private bool ShouldKeepVisibleTerminalFailureBeforeAutoRestart()
    {
        if (disposed)
        {
            return false;
        }

        var flow = sessionRuntime.FlowSnapshot;
        if (flow.TerminalKind == SessionTerminalKind.Failed &&
            (string.Equals(flow.FailureReason, "session_liveness_timeout", StringComparison.Ordinal) ||
             string.Equals(flow.TerminalStatusText, "Connection lost.", StringComparison.Ordinal)))
        {
            return true;
        }

        return sessionRuntime.State == SessionRuntimeState.Failed &&
               string.Equals(sessionRuntime.StatusText, "Connection lost.", StringComparison.Ordinal);
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

    private bool CanOpenDiagnosticsCommand()
    {
        return CanOpenDiagnostics;
    }

    private bool CanTriggerEndSession()
    {
        return CanEndSession && !localEndCommandInFlight && !suppressConnectedControlsAfterLocalEnd;
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
            var latency = screenShareCoordinator.GetDebugLatencySnapshotAndReset();
            Debug.WriteLine(
                $"[ScreenSharePreviewVm] Snapshot heap={heapBytes} ws={process.WorkingSet64} decoded={screenShareCoordinator.FramesDecoded} state={ScreenSharePreviewStatus.State} " +
                $"decode={FormatLatency(latency.DecodeDuration)} e2e={FormatLatency(latency.EndToEnd)}.");
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

    private async Task CopyInviteAsync()
    {
        if (clipboardService is null)
        {
            copyFeedback.Show("Couldn't copy the invite. Try again.");
            return;
        }

        EnsureInviteSnapshot(forceNewToken: false);
        if (string.IsNullOrWhiteSpace(ShareInvite))
        {
            copyFeedback.Show("Invite is not ready yet.");
            return;
        }

        try
        {
            await clipboardService.SetTextAsync(ShareInvite);
            copyFeedback.Show("Invite copied.");
        }
        catch
        {
            copyFeedback.Show("Couldn't copy the invite. Try again.");
        }
    }

    private async Task CopyAddressAsync()
    {
        if (clipboardService is null)
        {
            copyFeedback.Show("Could not copy address. Try again.");
            return;
        }

        EnsureInviteSnapshot(forceNewToken: false);
        if (string.IsNullOrWhiteSpace(ShareAddress))
        {
            copyFeedback.Show("Address is not ready yet.");
            return;
        }

        try
        {
            await clipboardService.SetTextAsync(ShareAddress);
            copyFeedback.Show("Address copied.");
        }
        catch
        {
            copyFeedback.Show("Could not copy address. Try again.");
        }
    }

    private async Task ShareInviteAsync()
    {
        EnsureInviteSnapshot(forceNewToken: false);
        if (string.IsNullOrWhiteSpace(ShareInvite))
        {
            copyFeedback.Show("Invite is not ready yet.");
            return;
        }

        try
        {
            var shared = await inviteShareService.ShareInviteAsync(ShareInvite, CancellationToken.None);
            if (shared.IsSuccess)
            {
                copyFeedback.Show("Choose how to share the invite.");
                return;
            }

            if (clipboardService is not null)
            {
                await clipboardService.SetTextAsync(ShareInvite);
                copyFeedback.Show("Share not available. Invite copied instead.");
                return;
            }

            copyFeedback.Show(shared.Message ?? "Couldn't share the invite.");
        }
        catch
        {
            if (clipboardService is not null)
            {
                try
                {
                    await clipboardService.SetTextAsync(ShareInvite);
                    copyFeedback.Show("Share not available. Invite copied instead.");
                    return;
                }
                catch
                {
                    // ignore and fall through
                }
            }

            copyFeedback.Show("Couldn't share the invite.");
        }
    }

    private void RefreshInvite()
    {
        startupFailureBlocksAutoRestart = false;
        EnsureInviteSnapshot(forceNewToken: true);
    }

    private bool CanAllowIncomingRequest()
    {
        return HasIncomingRequest &&
               !IsRequestAllowed &&
               GetSelectedIncomingApprovalCapabilities() != CapabilityGrant.None;
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

        suppressConnectedControlsAfterLocalEnd = false;

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

        var sendAttempt = Interlocked.Increment(ref chatSendAttemptCounter);
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
        if (sendAttempt == Interlocked.Read(ref chatSendAttemptCounter) &&
            string.IsNullOrWhiteSpace(ChatDraft))
        {
            ChatDraft = draft;
        }
    }

    private void CancelAndGoBack()
    {
        StopLocalScreenSharePreviewUiImmediately("local_stop");
        backAction();
    }

    private void EndSession()
    {
        if (localEndCommandInFlight)
        {
            return;
        }

        localEndCommandInFlight = true;
        suppressConnectedControlsAfterLocalEnd = true;
        IsChatInputEnabled = false;
        CanEndSession = false;
        CanSendFiles = false;
        EndSessionCommand.NotifyCanExecuteChanged();
        uiRecoveryTransientDismissed = true;
        ClearUiRecoveryTransient();
        ShowTransientBanner = false;
        TransientBannerText = string.Empty;
        CanCancelTransient = false;
        sessionRuntime.NotifyLocalEndRequested();
        StopLocalScreenSharePreviewUiImmediately("local_stop");
        ApplyTerminalPresentationFromFlow(sessionRuntime.FlowSnapshot);
        uiStateStore?.SetPhase(SessionUiPhase.Waiting, "UserEndSession:ReturnToWaiting");
        EffectivePhase = SessionUiPhase.Waiting;
        SendChatCommand.NotifyCanExecuteChanged();
        EndSessionCommand.NotifyCanExecuteChanged();
        AssertUiConsistency();
        _ = DisconnectAfterLocalEndAsync();
    }

    private async Task DisconnectAfterLocalEndAsync()
    {
        try
        {
            await sessionRuntime.DisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Helpee local end-session disconnect failed: {ex.Message}");
        }
    }

    private void RequestRemoteControl()
    {
        // Helpee never initiates control in P2.
    }

    private void StopRemoteControl()
    {
        _ = StopRemoteControlAsync();
    }

    private async Task StopRemoteControlAsync()
    {
        if (!CanStopRemoteControlAction())
        {
            return;
        }

        await sessionRuntime.StopRemoteControlAsync("UserStop", CancellationToken.None);
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

    private async Task AllowControlConsentAsync()
    {
        await RespondToControlConsentAsync(allow: true).ConfigureAwait(false);
    }

    private async Task DenyControlConsentAsync()
    {
        await RespondToControlConsentAsync(allow: false).ConfigureAwait(false);
    }

    private async Task RespondToControlConsentAsync(bool allow)
    {
        if (!CanRespondToControlConsent())
        {
            return;
        }

        var decision = allow ? "allow" : "deny";
        var requestId = sessionRuntime.CurrentControlRequestId ?? string.Empty;
        LogHelpeeControlConsentEvent("helpee_control_consent_clicked", decision, requestId, "clicked");
        SetRemoteControlConsentFeedback(string.Empty);
        SetRemoteControlConsentActionInFlight(true);

        try
        {
            var responded = await sessionRuntime
                .RespondToRemoteControlRequestAsync(allow, CancellationToken.None)
                .ConfigureAwait(false);

            await UiThreadDispatch.RunAsync(() =>
            {
                SyncFromRuntime();
                var result = ShowRemoteControlConsentDialog ? "failed" : "ignored";
                if (responded)
                {
                    result = "success";
                }
                else if (ShowRemoteControlConsentDialog)
                {
                    SetRemoteControlConsentFeedback("Couldn't send the control response.");
                }

                LogHelpeeControlConsentEvent("helpee_control_consent_completed", decision, requestId, result);
            });
        }
        catch (Exception ex)
        {
            await UiThreadDispatch.RunAsync(() =>
            {
                SetRemoteControlConsentFeedback("Couldn't send the control response.");
                LogHelpeeControlConsentEvent("helpee_control_consent_failed", decision, requestId, ex.GetType().Name);
            });
        }
        finally
        {
            await UiThreadDispatch.RunAsync(() => SetRemoteControlConsentActionInFlight(false));
        }
    }

    private bool CanRequestRemoteControlAction()
    {
        return false;
    }

    private bool CanStopRemoteControlAction()
    {
        return CanStopControl;
    }

    private bool CanRestartAsAdministratorAction()
    {
        return CanRestartAsAdministrator;
    }

    private void RestartAsAdministrator()
    {
        if (!CanRestartAsAdministratorAction())
        {
            return;
        }

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return;
        }

        try
        {
            Process.Start(
                new ProcessStartInfo
                {
                    FileName = processPath,
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = AppContext.BaseDirectory,
                });

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
                return;
            }

            Environment.Exit(0);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // UAC prompt canceled by user.
        }
        catch
        {
            // Best-effort.
        }
    }

    private bool CanRespondToControlConsent()
    {
        return ShowRemoteControlConsentDialog &&
               !remoteControlConsentActionInFlight &&
               sessionRuntime.ControlState == ControlState.Requesting &&
               sessionRuntime.Role == SessionRuntimeRole.Helpee;
    }

    private void NotifyRemoteControlConsentUiChanged()
    {
        var currentRequestId = ShowRemoteControlConsentDialog
            ? sessionRuntime.CurrentControlRequestId ?? string.Empty
            : string.Empty;
        if (!string.Equals(lastRemoteControlConsentRequestId, currentRequestId, StringComparison.Ordinal))
        {
            lastRemoteControlConsentRequestId = currentRequestId;
            remoteControlConsentFeedbackText = string.Empty;
            remoteControlConsentActionInFlight = false;
        }

        if (!ShowRemoteControlConsentDialog &&
            (remoteControlConsentActionInFlight || !string.IsNullOrWhiteSpace(remoteControlConsentFeedbackText)))
        {
            remoteControlConsentActionInFlight = false;
            remoteControlConsentFeedbackText = string.Empty;
            lastRemoteControlConsentRequestId = string.Empty;
        }

        OnPropertyChanged(nameof(ShowRemoteControlConsentDialog));
        OnPropertyChanged(nameof(RemoteControlConsentFeedbackText));
        OnPropertyChanged(nameof(ShowRemoteControlConsentFeedback));
        OnPropertyChanged(nameof(CanSubmitRemoteControlConsent));
        AllowControlConsentCommand?.NotifyCanExecuteChanged();
        DenyControlConsentCommand?.NotifyCanExecuteChanged();
    }

    private void SetRemoteControlConsentActionInFlight(bool value)
    {
        if (remoteControlConsentActionInFlight == value)
        {
            return;
        }

        remoteControlConsentActionInFlight = value;
        OnPropertyChanged(nameof(CanSubmitRemoteControlConsent));
        AllowControlConsentCommand?.NotifyCanExecuteChanged();
        DenyControlConsentCommand?.NotifyCanExecuteChanged();
    }

    private void SetRemoteControlConsentFeedback(string value)
    {
        value ??= string.Empty;
        if (string.Equals(remoteControlConsentFeedbackText, value, StringComparison.Ordinal))
        {
            return;
        }

        remoteControlConsentFeedbackText = value;
        OnPropertyChanged(nameof(RemoteControlConsentFeedbackText));
        OnPropertyChanged(nameof(ShowRemoteControlConsentFeedback));
    }

    private void LogHelpeeControlConsentEvent(string eventName, string decision, string requestId, string result)
    {
        LocalOperationalLog.Info(
            "HelpeeUi",
            $"event={eventName}; decision={decision}; request_id={SanitizeForLog(requestId)}; control_state={sessionRuntime.ControlState}; has_pending_prompt={sessionRuntime.HasPendingRemoteControlConsentPrompt}; result={result}");
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
        RestartWaitingSession(
            preserveHelperIdentityForRetry: true,
            preservePeerEndedNotice: false);
    }

    private void StartHosting(bool allowAfterVisibleTerminalFailure = false)
    {
        if (!allowAfterVisibleTerminalFailure &&
            ShouldKeepVisibleTerminalFailureBeforeAutoRestart())
        {
            AppLog.Info("Helpee hosting auto-restart blocked; reason=visible_terminal_failure");
            return;
        }

        PrepareForNewSession();
        EnsureInviteSnapshot(forceNewToken: false);

        if (IsStartupBlocked || startupFailureBlocksAutoRestart)
        {
            return;
        }

        CancelIncomingRequestTimeout();
        reliabilityAttempt = SessionReliabilityLog.StartAttempt("Helpee", transportConfig.Key);
        sessionRuntime.SetReliabilityAttempt(reliabilityAttempt);
        LogReliability(SessionReliabilityStage.DiscoveryStarted);

        AppLog.Info($"Helpee hosting using {transportConfig.Key} with address-native mode");
        _ = StartHostingAsync();
    }

    private async Task StartHostingAsync()
    {
        try
        {
            await sessionRuntime.ResetAsync();
            await sessionRuntime.StartHelpeeAsync(CancellationToken.None);
            await UiThreadDispatch.RunAsync(() =>
            {
                RefreshAutomaticIdentityRecoveryWarning();
                EnsureInviteSnapshot(forceNewToken: false);
            });
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
                    RefreshAutomaticIdentityRecoveryWarning();
                    var message = string.IsNullOrWhiteSpace(sessionRuntime.StatusText)
                        ? "Could not start. Refresh invite and try again."
                        : sessionRuntime.StatusText;
                    if (IsProtectedSeedStorageReadFailure(message))
                    {
                        startupFailureBlocksAutoRestart = true;
                        autoRegeneratingAfterDisconnect = false;
                    }

                    ConnectionStatus = message;
                    ConnectionState = sessionRuntime.State is SessionRuntimeState.Failed or SessionRuntimeState.Disconnected
                        ? "Failed"
                        : "Disconnected";
                    if (!HasShareInvite || IsProtectedSeedStorageReadFailure(message))
                    {
                        UpdateShareInviteStatusText(message);
                    }
                });
            }
        }
    }

    private void EnsureInviteSnapshot(bool forceNewToken)
    {
        var candidateAddress = ResolveInviteAddress();
        var boundHelperAddress = ResolveVerifiedInviteBindingAddress();
        var boundHelperIdentity = boundHelperAddress?.Value ?? string.Empty;
        if (!PeerAddress.TryParse(candidateAddress, out var peerAddress))
        {
            UpdateShareAddressText(string.Empty);
            UpdateShareInviteText(string.Empty);
            UpdateShareInviteRawTokenText(string.Empty);
            UpdateShareInviteStatusText(startupFailureBlocksAutoRestart && IsProtectedSeedStorageReadFailure(sessionRuntime.StatusText)
                ? sessionRuntime.StatusText
                : "Preparing invite…");
            shareInviteExpiresAtUtc = DateTimeOffset.MinValue;
            shareInviteAutoRefreshTriggered = false;
            UpdateShareInviteExpiryText(string.Empty);
            lastInviteAddressForToken = string.Empty;
            lastInviteHelperIdentityForToken = string.Empty;
            RefreshShareQrBitmaps();
            return;
        }

        UpdateShareAddressText(peerAddress.Value);
        if (!forceNewToken &&
            string.Equals(lastInviteAddressForToken, peerAddress.Value, StringComparison.Ordinal) &&
            string.Equals(lastInviteHelperIdentityForToken, boundHelperIdentity, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(shareInviteText))
        {
            UpdateShareInviteStatusText("Invite ready");
            return;
        }

        if (!SessionId.TryParse($"sess_{Guid.NewGuid():N}", out var sessionId))
        {
            UpdateShareInviteText(string.Empty);
            UpdateShareInviteRawTokenText(string.Empty);
            UpdateShareInviteStatusText("Couldn't prepare the invite right now.");
            shareInviteExpiresAtUtc = DateTimeOffset.MinValue;
            shareInviteAutoRefreshTriggered = false;
            UpdateShareInviteExpiryText(string.Empty);
            lastInviteHelperIdentityForToken = string.Empty;
            RefreshShareQrBitmaps();
            return;
        }

        if (boundHelperAddress is null && !IsUnboundPublicInviteFlowAvailable)
        {
            UpdateShareInviteText(string.Empty);
            UpdateShareInviteRawTokenText(string.Empty);
            UpdateShareInviteStatusText("Invite setup requires a verified helper address.");
            shareInviteExpiresAtUtc = DateTimeOffset.MinValue;
            shareInviteAutoRefreshTriggered = false;
            UpdateShareInviteExpiryText(string.Empty);
            lastInviteAddressForToken = string.Empty;
            lastInviteHelperIdentityForToken = string.Empty;
            AppLog.Warn("Helpee invite generation blocked; reason=helper_identity_required_for_public_flow");
            RefreshShareQrBitmaps();
            return;
        }

        var created = inviteTokenFactory.Create(
            new InviteTokenCreateRequest(
                IssuerAddress: peerAddress,
                TargetAddress: peerAddress,
                SessionId: sessionId,
                Capabilities: InviteCapabilities.Chat | InviteCapabilities.ScreenShare | InviteCapabilities.RemoteControl | InviteCapabilities.FileTransfer,
                Lifetime: DefaultInviteLifetime,
                BoundHelperAddress: boundHelperAddress),
            DateTimeOffset.UtcNow);

        if (!created.IsSuccess || string.IsNullOrWhiteSpace(created.Token))
        {
            UpdateShareInviteText(string.Empty);
            UpdateShareInviteRawTokenText(string.Empty);
            UpdateShareInviteStatusText(
                created.Error == InviteTokenCreateError.Throttled
                    ? "Please wait a moment before refreshing again."
                    : "Couldn't prepare the invite right now.");
            shareInviteExpiresAtUtc = DateTimeOffset.MinValue;
            shareInviteAutoRefreshTriggered = false;
            UpdateShareInviteExpiryText(string.Empty);
            lastInviteHelperIdentityForToken = string.Empty;
            AppLog.Info($"Helpee invite token refresh failed ({created.Error}): {created.Message ?? "(none)"}");
            RefreshShareQrBitmaps();
            return;
        }

        UpdateShareInviteText(created.Token!);
        UpdateShareInviteRawTokenText(created.Token!);
        UpdateShareInviteStatusText("Invite ready");
        shareInviteExpiresAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(created.Payload!.ExpiresAtUtcMs);
        shareInviteAutoRefreshTriggered = false;
        UpdateShareInviteExpiryText(BuildInviteExpiryText(DateTimeOffset.UtcNow));
        lastInviteAddressForToken = peerAddress.Value;
        lastInviteHelperIdentityForToken = boundHelperIdentity;
        RefreshShareQrBitmaps();
    }

    private string? ResolveInviteAddress()
    {
        return sessionRuntime.SecurityState.HelpeeAddress?.Value ??
               sessionRuntime.CurrentInvitePeerAddress?.Value ??
               sessionRuntime.CurrentLocalPeerAddress?.Value;
    }

    internal void SetVerifiedInviteHelperIdentity(
        PeerAddress? helperIdentity,
        PeerAddress? helperTargetAddress = null,
        bool refreshInvite = true,
        string? normalizedInputOverride = null,
        PeerAddress? verificationIdentity = null)
    {
        var normalized = helperIdentity?.Value ?? string.Empty;
        var normalizedVerificationIdentity = verificationIdentity?.Value ?? string.Empty;
        var normalizedTargetAddress = helperTargetAddress?.Value ?? normalized;
        suppressAutoApplyInviteHelperIdentityInput = true;
        try
        {
            InviteHelperIdentityInput = normalizedInputOverride ?? normalized;
        }
        finally
        {
            suppressAutoApplyInviteHelperIdentityInput = false;
        }

        if (string.Equals(verifiedInviteHelperIdentity, normalized, StringComparison.Ordinal) &&
            string.Equals(verifiedHelpRequestTargetAddress, normalizedTargetAddress, StringComparison.Ordinal) &&
            string.Equals(verifiedInviteVerificationIdentity, normalizedVerificationIdentity, StringComparison.Ordinal))
        {
            if (refreshInvite)
            {
                EnsureInviteSnapshot(forceNewToken: true);
            }

            return;
        }

        verifiedInviteHelperIdentity = normalized;
        verifiedInviteVerificationIdentity = normalizedVerificationIdentity;
        verifiedHelpRequestTargetAddress = normalizedTargetAddress;
        OnPropertyChanged(nameof(HasVerifiedInviteHelperIdentity));
        OnPropertyChanged(nameof(ShowWaitingInviteActions));
        OnPropertyChanged(nameof(InviteHelperIdentityStatusText));
        OnPropertyChanged(nameof(ShowVerifiedInviteHelperIdentity));
        OnPropertyChanged(nameof(VerifiedInviteHelperIdentityText));
        OnPropertyChanged(nameof(VerifiedInviteHelperVerificationCode));
        OnPropertyChanged(nameof(HasVerifiedInviteHelperVerificationCode));
        OnPropertyChanged(nameof(HeaderVerificationCodeText));
        OnPropertyChanged(nameof(ShowHeaderVerificationCode));
        OnPropertyChanged(nameof(FirstPillVerificationCodeText));
        OnPropertyChanged(nameof(ShowFirstPillVerificationCode));
        OnPropertyChanged(nameof(VerifiedInviteTechnicalHelperIdentityText));
        OnPropertyChanged(nameof(HasVerifiedInviteTechnicalHelperIdentity));
        OnPropertyChanged(nameof(CanRequestHelpAction));
        OnPropertyChanged(nameof(CanApplyInviteHelperIdentityAction));
        OnPropertyChanged(nameof(CanClearInviteHelperIdentityAction));
        ApplyInviteHelperIdentityCommand.NotifyCanExecuteChanged();
        ClearInviteHelperIdentityCommand.NotifyCanExecuteChanged();
        RequestHelpCommand.NotifyCanExecuteChanged();
        if (refreshInvite)
        {
            EnsureInviteSnapshot(forceNewToken: true);
        }
    }

    private void ApplyInviteHelperIdentity()
    {
        if (!TryResolveInviteHelperIdentityInput(out var helperIdentity, out var helperTargetAddress, out var normalizedInput, out var verificationIdentity))
        {
            return;
        }

        SetVerifiedInviteHelperIdentity(
            helperIdentity,
            helperTargetAddress,
            refreshInvite: true,
            normalizedInputOverride: normalizedInput,
            verificationIdentity: verificationIdentity);
    }

    private void AutoApplyInviteHelperIdentityIfPossible()
    {
        if (!CanStartOrConnect)
        {
            return;
        }

        if (!TryResolveInviteHelperIdentityInput(out var helperIdentity, out var helperTargetAddress, out var normalizedInput, out var verificationIdentity))
        {
            return;
        }

        if (string.Equals(verifiedInviteHelperIdentity, helperIdentity.Value, StringComparison.Ordinal))
        {
            return;
        }

        SetVerifiedInviteHelperIdentity(
            helperIdentity,
            helperTargetAddress,
            refreshInvite: true,
            normalizedInputOverride: normalizedInput,
            verificationIdentity: verificationIdentity);
    }

    private bool CanApplyInviteHelperIdentity()
    {
        return CanStartOrConnect &&
               TryResolveInviteHelperIdentityInput(out var helperIdentity, out _, out _, out _) &&
               !string.Equals(verifiedInviteHelperIdentity, helperIdentity.Value, StringComparison.Ordinal);
    }

    private void ClearInviteHelperIdentity()
    {
        InviteHelperIdentityInput = string.Empty;
        SetVerifiedInviteHelperIdentity(null, refreshInvite: true);
    }

    private bool CanClearInviteHelperIdentity()
    {
        return CanStartOrConnect &&
               (HasVerifiedInviteHelperIdentity || !string.IsNullOrWhiteSpace(InviteHelperIdentityInput));
    }

    private bool CanRequestHelp()
    {
        return CanStartOrConnect &&
               !sessionRuntime.HasPendingOutboundHelpRequest &&
               sessionRuntime.PendingOutboundHelpRequestDecision?.Accepted != true &&
               !hasIncomingRequest &&
               CurrentInviteHelperInputMatchesVerifiedIdentity() &&
               ResolveVerifiedHelpRequestTargetAddress() is not null &&
               HasShareInvite;
    }

    private bool CurrentInviteHelperInputMatchesVerifiedIdentity()
    {
        var currentInput = InviteHelperIdentityInput.Trim();
        if (!HasVerifiedInviteHelperIdentity ||
            string.IsNullOrWhiteSpace(currentInput))
        {
            return false;
        }

        if (string.Equals(currentInput, verifiedInviteHelperIdentity, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return TryResolveInviteHelperIdentityInput(out var helperIdentity, out _, out _, out _) &&
               string.Equals(helperIdentity.Value, verifiedInviteHelperIdentity, StringComparison.Ordinal);
    }

    public void ApplyHelperBootstrapInput(string input, string sourceLabel)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        InviteHelperIdentityInput = input.Trim();
        copyFeedback.Show(sourceLabel switch
        {
            "clipboard" => "Helper ID pasted.",
            "qr" => "Helper QR imported.",
            _ => "Helper ID added."
        });
    }

    private async Task RequestHelpAsync()
    {
        EnsureInviteSnapshot(forceNewToken: false);
        if (ResolveVerifiedHelpRequestTargetAddress() is not { } helperAddress ||
            string.IsNullOrWhiteSpace(ShareInvite))
        {
            UpdateShareInviteStatusText("Enter a valid helper address first.");
            return;
        }

        ClearFailurePresentation();
        suppressConnectedControlsAfterLocalEnd = false;
        ConnectionState = "Waiting";

        try
        {
            await sessionRuntime.RequestHelpAsync(helperAddress, ShareInvite, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Helpee help request failed: {ex.Message}");
            await UiThreadDispatch.RunAsync(() =>
            {
                RefreshAutomaticIdentityRecoveryWarning();
                ConnectionStatus = "Waiting for helper…";
                UpdateShareInviteStatusText(
                    ex is TimeoutException
                        ? "Couldn't reach the helper. Check the helper address and try again."
                        : "Couldn't send the help request right now. Please try again.");
            });
            return;
        }

        await UiThreadDispatch.RunAsync(() =>
        {
            ConnectionStatus = "Waiting for helper approval…";
            UpdateShareInviteStatusText("Request sent.");
        });
    }

    private bool TryResolveInviteHelperIdentityInput(
        out PeerAddress helperIdentity,
        out PeerAddress helperTargetAddress,
        out string normalizedInput,
        out PeerAddress? verificationIdentity)
    {
        normalizedInput = InviteHelperIdentityInput.Trim();
        verificationIdentity = null;
        if (HelperBootstrapQrPayload.TryParse(normalizedInput, out var bootstrapPayload) &&
            bootstrapPayload is not null)
        {
            helperTargetAddress = bootstrapPayload.HelperAddress;
            normalizedInput = HelperBootstrapQrPayload.Format(bootstrapPayload);
            if (!string.IsNullOrWhiteSpace(bootstrapPayload.HelperId))
            {
                var decodeResult = HelperIdentityTokenCodec.Decode(bootstrapPayload.HelperId);
                if (decodeResult.IsSuccess && decodeResult.Address is not null)
                {
                    helperIdentity = decodeResult.Address.Value;
                    verificationIdentity = helperIdentity;
                    return true;
                }
            }

            helperIdentity = bootstrapPayload.HelperAddress;
            return true;
        }

        if (normalizedInput.StartsWith(HelperIdentityTokenCodec.TokenPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var decodeResult = HelperIdentityTokenCodec.Decode(normalizedInput);
            if (decodeResult.IsSuccess && decodeResult.Address is not null)
            {
                helperIdentity = decodeResult.Address.Value;
                helperTargetAddress = helperIdentity;
                verificationIdentity = helperIdentity;
                normalizedInput = HelperIdentityTokenCodec.Encode(helperIdentity);
                return true;
            }

            helperIdentity = default;
            helperTargetAddress = default;
            return false;
        }

        if (PeerAddress.TryParse(normalizedInput, out helperIdentity))
        {
            helperTargetAddress = helperIdentity;
            normalizedInput = helperIdentity.Value;
            return true;
        }

        helperIdentity = default;
        helperTargetAddress = default;
        return false;
    }

    private PeerAddress? ResolveVerifiedInviteBindingAddress()
    {
        return PeerAddress.TryParse(verifiedInviteHelperIdentity, out var parsed)
            ? parsed
            : null;
    }

    private PeerAddress? ResolveVerifiedHelpRequestTargetAddress()
    {
        if (PeerAddress.TryParse(verifiedHelpRequestTargetAddress, out var parsed))
        {
            return parsed;
        }

        return ResolveVerifiedInviteBindingAddress();
    }

    private void UpdateShareInviteText(string value)
    {
        value ??= string.Empty;
        if (SetProperty(ref shareInviteText, value, nameof(ShareInvite)))
        {
            OnPropertyChanged(nameof(HasShareInvite));
            OnPropertyChanged(nameof(ShowShareInviteExpiry));
            OnPropertyChanged(nameof(ShowShareInviteStatus));
            OnPropertyChanged(nameof(ShowWaitingInviteActions));
            OnPropertyChanged(nameof(CanRequestHelpAction));
            RequestHelpCommand.NotifyCanExecuteChanged();
        }
    }

    private void UpdateShareInviteRawTokenText(string value)
    {
        value ??= string.Empty;
        if (SetProperty(ref shareInviteRawTokenText, value, nameof(ShareInviteRawToken)))
        {
            OnPropertyChanged(nameof(HasShareInviteRawToken));
        }
    }

    private void UpdateShareAddressText(string value)
    {
        value ??= string.Empty;
        if (SetProperty(ref shareAddressText, value, nameof(ShareAddress)))
        {
            OnPropertyChanged(nameof(HasShareAddress));
        }
    }

    private void UpdateShareInviteStatusText(string value)
    {
        value = ComposeShareInviteStatusText(value);
        if (SetProperty(ref shareInviteStatusText, value, nameof(ShareInviteStatusText)))
        {
            OnPropertyChanged(nameof(ShowShareInviteStatus));
        }
    }

    private string ComposeShareInviteStatusText(string? value)
    {
        var normalized = value ?? string.Empty;
        if (string.IsNullOrWhiteSpace(automaticIdentityRecoveryWarning) ||
            string.IsNullOrWhiteSpace(normalized) ||
            IsProtectedSeedStorageReadFailure(normalized) ||
            normalized.Contains(automaticIdentityRecoveryWarning, StringComparison.Ordinal))
        {
            return normalized;
        }

        return $"{automaticIdentityRecoveryWarning} {normalized}";
    }

    private void RefreshAutomaticIdentityRecoveryWarning()
    {
        var warning = NknIdentityStore.GetAutomaticRecoveryUserWarning();
        if (string.IsNullOrWhiteSpace(warning) ||
            string.Equals(automaticIdentityRecoveryWarning, warning, StringComparison.Ordinal))
        {
            return;
        }

        automaticIdentityRecoveryWarning = warning;
        OnPropertyChanged(nameof(ShowShareInviteStatus));
    }

    private void UpdateShareInviteExpiryText(string value)
    {
        value ??= string.Empty;
        if (SetProperty(ref shareInviteExpiryText, value, nameof(ShareInviteExpiryText)))
        {
            OnPropertyChanged(nameof(ShowShareInviteExpiry));
        }
    }

    private void UpdateIncomingRequestTimeoutText(string value)
    {
        value ??= string.Empty;
        if (SetProperty(ref incomingRequestTimeoutText, value, nameof(IncomingRequestTimeoutText)))
        {
            OnPropertyChanged(nameof(ShowIncomingRequestTimeout));
        }
    }

    private void RefreshShareQrBitmaps()
    {
        RefreshShareInviteQrBitmap(ShareInvite);
    }

    private void RefreshShareInviteQrBitmap(string? text)
    {
        var previousRefresh = shareInviteQrRefreshCts;
        shareInviteQrRefreshCts = null;
        previousRefresh?.Cancel();
        previousRefresh?.Dispose();

        if (string.IsNullOrWhiteSpace(text))
        {
            ReplaceShareQrBitmap(
                ref shareInviteQrBitmap,
                next: null,
                nameof(ShareInviteQrImage),
                nameof(ShowShareInviteQr),
                nameof(ShowShareInviteQrPlaceholder));
            return;
        }

        var inviteSnapshot = text.Trim();
        var qrPayload = InviteQrPayload.Format(inviteSnapshot);
        var refreshCts = new CancellationTokenSource();
        var refreshVersion = unchecked(++shareInviteQrRefreshVersion);
        shareInviteQrRefreshCts = refreshCts;
        _ = RefreshShareInviteQrBitmapAsync(inviteSnapshot, qrPayload, refreshVersion, refreshCts);
    }

    private async Task RefreshShareInviteQrBitmapAsync(string inviteText, string qrPayload, int refreshVersion, CancellationTokenSource refreshCts)
    {
        try
        {
            var pngBytes = await Task.Run(() =>
            {
                if (refreshCts.IsCancellationRequested ||
                    !qrCodeService.TryCreatePng(qrPayload, out var generatedBytes, out _))
                {
                    return Array.Empty<byte>();
                }

                return generatedBytes;
            }, refreshCts.Token).ConfigureAwait(false);

            if (disposed ||
                refreshCts.IsCancellationRequested ||
                refreshVersion != shareInviteQrRefreshVersion ||
                pngBytes.Length == 0)
            {
                return;
            }

            using var stream = new MemoryStream(pngBytes);
            var nextBitmap = new Bitmap(stream);

            await UiThreadDispatch.RunAsync(() =>
            {
                if (disposed ||
                    refreshCts.IsCancellationRequested ||
                    refreshVersion != shareInviteQrRefreshVersion ||
                    !string.Equals(ShareInvite, inviteText, StringComparison.Ordinal))
                {
                    nextBitmap.Dispose();
                    return;
                }

                ReplaceShareQrBitmap(
                    ref shareInviteQrBitmap,
                    nextBitmap,
                    nameof(ShareInviteQrImage),
                    nameof(ShowShareInviteQr),
                    nameof(ShowShareInviteQrPlaceholder));
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // No-op.
        }
        catch
        {
            // Leave the placeholder visible and let status text communicate invite readiness.
        }
        finally
        {
            if (ReferenceEquals(shareInviteQrRefreshCts, refreshCts))
            {
                shareInviteQrRefreshCts = null;
            }

            refreshCts.Dispose();
        }
    }

    private void ReplaceShareQrBitmap(ref Bitmap? field, Bitmap? next, string bitmapPropertyName, string visiblePropertyName, string? placeholderPropertyName = null)
    {
        var previous = field;
        field = next;
        previous?.Dispose();
        OnPropertyChanged(bitmapPropertyName);
        OnPropertyChanged(visiblePropertyName);
        if (!string.IsNullOrWhiteSpace(placeholderPropertyName))
        {
            OnPropertyChanged(placeholderPropertyName);
        }
    }

    private static bool IsUnboundPublicInviteFlowAvailable
    {
        get
        {
#if DEBUG
            return true;
#else
            return AppFeatureFlags.AllowInsecureUnboundPublicInvites;
#endif
        }
    }

    private void OnShareInviteExpiryTimerTick(object? sender, EventArgs e)
    {
        if (disposed)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        RefreshIncomingRequestTimeoutText(now);

        if (!HasShareInvite || shareInviteExpiresAtUtc == DateTimeOffset.MinValue)
        {
            UpdateShareInviteExpiryText(string.Empty);
            return;
        }

        var remaining = shareInviteExpiresAtUtc - now;
        if (remaining <= TimeSpan.Zero)
        {
            if (!shareInviteAutoRefreshTriggered)
            {
                shareInviteAutoRefreshTriggered = true;
                EnsureInviteSnapshot(forceNewToken: true);
            }

            UpdateShareInviteExpiryText("Updating invite…");
            return;
        }

        if (!shareInviteAutoRefreshTriggered && remaining <= TimeSpan.FromSeconds(10))
        {
            shareInviteAutoRefreshTriggered = true;
            EnsureInviteSnapshot(forceNewToken: true);
            UpdateShareInviteExpiryText("Updating invite…");
            return;
        }

        UpdateShareInviteExpiryText(BuildInviteExpiryText(now));
    }

    private void RefreshIncomingRequestTimeoutText(DateTimeOffset now)
    {
        if (!HasIncomingRequest ||
            !IsIncomingRequestView ||
            incomingRequestExpiresAtUtc == DateTimeOffset.MinValue)
        {
            UpdateIncomingRequestTimeoutText(string.Empty);
            return;
        }

        UpdateIncomingRequestTimeoutText(BuildIncomingRequestTimeoutText(now));
    }

    private string BuildInviteExpiryText(DateTimeOffset now)
    {
        if (shareInviteExpiresAtUtc == DateTimeOffset.MinValue)
        {
            return string.Empty;
        }

        var remaining = shareInviteExpiresAtUtc - now;
        if (remaining <= TimeSpan.Zero)
        {
            return "Invite expired.";
        }

        var minutes = (int)remaining.TotalMinutes;
        var seconds = remaining.Seconds;
        if (remaining <= TimeSpan.FromMinutes(1))
        {
            return $"Invite expires in {minutes:D2}:{seconds:D2} (auto-refreshing)";
        }

        return $"Invite expires in {minutes:D2}:{seconds:D2}";
    }

    private string BuildIncomingRequestTimeoutText(DateTimeOffset now)
    {
        if (incomingRequestExpiresAtUtc == DateTimeOffset.MinValue)
        {
            return string.Empty;
        }

        var remaining = incomingRequestExpiresAtUtc - now;
        var totalSeconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;
        return $"Request expires in {minutes:D2}:{seconds:D2}.";
    }

    private async Task ApproveIncomingRequestAsync()
    {
        await sessionRuntime.ApproveAsync(GetSelectedIncomingApprovalCapabilities(), CancellationToken.None);
        await UiThreadDispatch.RunAsync(() =>
        {
            ShowChatNotice = false;
            SyncFromRuntime();
        });
    }

    private void OnIncomingJoinRequestAvailable(object? sender, EventArgs e)
    {
        LogReliability(SessionReliabilityStage.IncomingJoinRequest);
        _ = UiThreadDispatch.RunAsync(StartIncomingRequestTimeout);
    }

    private void OnRuntimeDisconnected(object? sender, EventArgs e)
    {
        if (disposed)
        {
            return;
        }

        _ = UiThreadDispatch.RunAsync(() =>
        {
            if (HasIncomingRequest && !IsRequestAllowed)
            {
                CancelIncomingRequestTimeout();
                HasIncomingRequest = false;
                IsRequestAllowed = false;
                RestartWaitingSession(
                    preserveHelperIdentityForRetry: true,
                    preservePeerEndedNotice: false);

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

    private void OnRemoteSessionEnded(object? sender, EventArgs e)
    {
        if (disposed)
        {
            return;
        }

        _ = UiThreadDispatch.RunAsync(() =>
        {
            StopLocalScreenSharePreviewUiImmediately("remote_session_ended");
            SyncFromRuntime();
        });
    }

    private void OnScreenShareStopped(object? sender, EventArgs e)
    {
        if (disposed)
        {
            return;
        }

        _ = UiThreadDispatch.RunAsync(() => StopLocalScreenSharePreviewUiImmediately("screenshare_stopped"));
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
            if (!IsConnectedView)
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
                            "HelpeeUi",
                            $"event=file_transfer_ui_refresh_coalesced; role=Helpee; coalesced_count={coalescedCount}; min_interval_ms={FileTransferUiRefreshMinIntervalMs}");
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
            LocalOperationalLog.Warn("HelpeeUi", $"event=file_transfer_ui_refresh_failed; role=Helpee; error={ex.GetType().Name}");
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
            OnPropertyChanged(nameof(SessionSupportsRemoteControl));
            OnPropertyChanged(nameof(ShowStopControlAction));
            OnPropertyChanged(nameof(CanStopControl));
            OnPropertyChanged(nameof(ShowRemoteControlActiveStatus));
            OnPropertyChanged(nameof(ShowRemoteControlAdminWarning));
            OnPropertyChanged(nameof(RemoteControlAdminWarningText));
            OnPropertyChanged(nameof(CanRestartAsAdministrator));
            OnPropertyChanged(nameof(ShowRemoteControlPreviewActiveCue));
            OnPropertyChanged(nameof(HeaderStatusText));
            OnPropertyChanged(nameof(ShowTransientStatusPanel));
            NotifyRemoteControlDiagnosticsChanged();
            NotifyRemoteControlConsentUiChanged();
            StopControlCommand.NotifyCanExecuteChanged();
            RestartAsAdministratorCommand.NotifyCanExecuteChanged();
        });
    }

#if DEBUG
    private void OnRemoteControlInputReceived(object? sender, SessionRuntimeRemoteControlInputReceivedEventArgs e)
    {
        if (disposed || !RemoteControlDebugOverlayEnabled)
        {
            return;
        }

        _ = UiThreadDispatch.RunAsync(() => UpdateRemoteControlDebugOverlay(e.Message));
    }

    private void UpdateRemoteControlDebugOverlay(ControlInputMessageV1 message)
    {
        if (!RemoteControlDebugOverlayEnabled)
        {
            return;
        }

        if (message.Nx.HasValue && message.Ny.HasValue)
        {
            remoteControlDebugLastPointerText = string.Create(
                CultureInfo.InvariantCulture,
                $"{message.Nx.Value:0.###}, {message.Ny.Value:0.###}");
        }

        remoteControlDebugLastEventText = BuildRemoteControlDebugEventText(message);
        remoteControlDebugUpdatedText = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

        var nowMs = Environment.TickCount64;
        if (remoteControlDebugWindowStartTickMs == 0)
        {
            remoteControlDebugWindowStartTickMs = nowMs;
        }

        if (nowMs - remoteControlDebugWindowStartTickMs >= 1000)
        {
            remoteControlDebugEventsPerSecond = remoteControlDebugEventsInWindow;
            remoteControlDebugEventsInWindow = 0;
            remoteControlDebugWindowStartTickMs = nowMs;
        }

        remoteControlDebugEventsInWindow++;

        OnPropertyChanged(nameof(RemoteControlDebugPointerText));
        OnPropertyChanged(nameof(RemoteControlDebugEventText));
        NotifyRemoteControlDiagnosticsChanged();
        OnPropertyChanged(nameof(RemoteControlDebugStatsText));
        OnPropertyChanged(nameof(RemoteControlDebugUpdatedText));
    }

    private static string BuildRemoteControlDebugEventText(ControlInputMessageV1 message)
    {
        return message.Kind switch
        {
            "mouse_move" => "move",
            "mouse_button" => $"{message.Action ?? "button"} {message.Button ?? string.Empty}".Trim(),
            "mouse_wheel" => string.Create(
                CultureInfo.InvariantCulture,
                $"wheel {message.DeltaX.GetValueOrDefault():0.##},{message.DeltaY.GetValueOrDefault():0.##}"),
            "key" => $"{message.Action ?? "key"} {message.Key ?? "(none)"}",
            _ => message.Kind ?? "(unknown)",
        };
    }
#endif

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

        return (lastError, "The connection stopped. Refresh invite and try again.");
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
        EnsureInviteSnapshot(forceNewToken: false);
        SyncIncomingApprovalRequestFromRuntime();
        PromoteProtectedSeedStorageStartupFailureIfNeeded();

        var flow = sessionRuntime.FlowSnapshot;
        if (localEndCommandInFlight &&
            !flow.LocalEndInProgress &&
            flow.Phase is SessionFlowPhase.HelpeeWaiting or SessionFlowPhase.NoSession)
        {
            PrepareForNewSession();
        }

        var autoRegeneratedAfterDisconnect = false;
        if (TryApplyAcceptedHelpRequestFailureReset(flow))
        {
            autoRegeneratedAfterDisconnect = true;
        }
        else if (flow.ShowIncomingApproval)
        {
            ClearFailurePresentation();
            ConnectionStatus = SessionFlowViewProjection.ResolveStatusText(flow, transportConfig.AllowStatusText);
            ConnectionState = flow.DisplayConnectionState;
            ClearPeerEndedNotice();
            HasIncomingRequest = true;
            IsRequestAllowed = false;
        }
        else if (flow.TerminalKind != SessionTerminalKind.None)
        {
            if (TryApplyPostTerminalAction(flow))
            {
                autoRegeneratedAfterDisconnect = true;
            }
            else
            {
                ApplyTerminalPresentationFromFlow(flow);
            }
        }
        else
        {
            ClearFailurePresentation();
            ConnectionStatus = SessionFlowViewProjection.ResolveStatusText(flow, transportConfig.AllowStatusText);
            ConnectionState = flow.DisplayConnectionState;

            if (string.Equals(flow.DisplayConnectionState, "Connected", StringComparison.Ordinal))
            {
                ClearPeerEndedNotice();
                CancelIncomingRequestTimeout();
                HasIncomingRequest = false;
                IsRequestAllowed = true;
                ShowChatNotice = false;
            }
            else
            {
                if (HasIncomingRequest || IsRequestAllowed)
                {
                    CancelIncomingRequestTimeout();
                }

                HasIncomingRequest = false;
                IsRequestAllowed = false;
                ResetIncomingApprovalRequestState();
            }
        }

        if (flow.UiPhase is SessionUiPhase.Connected or SessionUiPhase.Waiting or SessionUiPhase.Idle)
        {
            uiRecoveryTransientDismissed = false;
            ClearUiRecoveryTransient();
        }

        if (flow.ShouldClearConversationUi)
        {
            RequestStopScreenSharePreview("runtime_flow_clear_conversation");
        }

        SessionUxContext? phaseContext = null;
        if (flow.UiPhase == SessionUiPhase.Failed &&
            (!string.IsNullOrWhiteSpace(flow.FailureTitle) ||
             !string.IsNullOrWhiteSpace(flow.FailureMessage) ||
             !string.IsNullOrWhiteSpace(flow.FailureActionText)))
        {
            phaseContext = new SessionUxContext(flow.FailureTitle, flow.FailureMessage, flow.FailureActionText);
        }

        uiStateStore?.SetPhase(
            autoRegeneratedAfterDisconnect ? SessionUiPhase.Waiting : flow.UiPhase,
            autoRegeneratedAfterDisconnect ? "SyncFromRuntime:Flow:AutoRegenerated" : $"SyncFromRuntime:Flow:{flow.Phase}",
            phaseContext);
        ApplySessionBannerPolicy();
        UpdateUiFromSnapshot();
        if (IsScreenSharingPreviewActive)
        {
            RequestTransportScreenShareSync(true, "runtime_sync");
        }

        OnPropertyChanged(nameof(ShowFailurePanel));
        OnPropertyChanged(nameof(IsChatReady));
        OnPropertyChanged(nameof(ShowHeaderCaptureDisplayPicker));
        OnPropertyChanged(nameof(SessionSupportsRemoteControl));
        OnPropertyChanged(nameof(ShowStopControlAction));
        OnPropertyChanged(nameof(CanStopControl));
        OnPropertyChanged(nameof(ShowRemoteControlActiveStatus));
        OnPropertyChanged(nameof(ShowRemoteControlAdminWarning));
        OnPropertyChanged(nameof(RemoteControlAdminWarningText));
        OnPropertyChanged(nameof(CanRestartAsAdministrator));
        OnPropertyChanged(nameof(ShowRemoteControlPreviewActiveCue));
        NotifySessionVerificationPropertiesChanged();
        NotifyRemoteControlDiagnosticsChanged();
        NotifyRemoteControlConsentUiChanged();
        RefreshCommandStates();
        SyncTransientStatusFromRuntime();
        NotifyStatusBannerDetailChanged();
    }

    private void ApplyTerminalPresentationFromFlow(SessionFlowSnapshot flow)
    {
        if (flow.ShouldClearConversationUi)
        {
            ClearSessionConversationUi();
            RequestStopScreenSharePreview("terminal_flow_clear_conversation");
        }

        ShowTransientBanner = false;
        TransientBannerText = string.Empty;
        CanCancelTransient = false;
        IsChatInputEnabled = false;
        CanEndSession = false;
        CanSendFiles = false;
        uiRecoveryTransientDismissed = flow.TerminalKind == SessionTerminalKind.PeerEnded || flow.TerminalKind == SessionTerminalKind.LocalEnded;
        FailureTitle = flow.FailureTitle;
        FailureMessage = flow.FailureMessage;
        FailureActionText = flow.FailureActionText;

        switch (flow.TerminalKind)
        {
            case SessionTerminalKind.LocalEnded:
                ClearPeerEndedNotice();
                CancelIncomingRequestTimeout();
                HasIncomingRequest = false;
                IsRequestAllowed = false;
                ShowChatNotice = false;
                ConnectionStatus = SessionFlowViewProjection.ResolveStatusText(flow, transportConfig.AllowStatusText);
                ConnectionState = flow.DisplayConnectionState;
                break;
            case SessionTerminalKind.PeerEnded:
                CancelIncomingRequestTimeout();
                HasIncomingRequest = false;
                IsRequestAllowed = false;
                ShowChatNotice = false;
                ConnectionStatus = SessionFlowViewProjection.ResolveStatusText(flow, transportConfig.AllowStatusText);
                ConnectionState = flow.DisplayConnectionState;
                TryShowPeerEndedNotice(flow);
                break;
            case SessionTerminalKind.Rejected:
                ClearPeerEndedNotice();
                CancelIncomingRequestTimeout();
                HasIncomingRequest = false;
                IsRequestAllowed = false;
                ConnectionStatus = string.IsNullOrWhiteSpace(flow.TerminalStatusText)
                    ? "Request was rejected."
                    : flow.TerminalStatusText;
                ConnectionState = "Failed";
                UpdateShareInviteStatusText("The helper declined the request.");
                break;
            case SessionTerminalKind.Failed:
                ClearPeerEndedNotice();
                CancelIncomingRequestTimeout();
                HasIncomingRequest = false;
                IsRequestAllowed = false;
                ConnectionStatus = string.IsNullOrWhiteSpace(flow.TerminalStatusText)
                    ? transportConfig.HelpeeDisconnectedText
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

    private bool TryApplyPostTerminalAction(SessionFlowSnapshot flow)
    {
        if (flow.PostTerminalAction == SessionFlowPostTerminalAction.None ||
            autoRegeneratingAfterDisconnect ||
            IsStartupBlocked ||
            startupFailureBlocksAutoRestart ||
            disposed)
        {
            return false;
        }

        var actionKey = $"{flow.PostTerminalAction}|{flow.TerminalKind}|{flow.SessionId ?? "(none)"}|{flow.TerminalStatusText}";
        if (string.Equals(lastAppliedPostTerminalActionKey, actionKey, StringComparison.Ordinal))
        {
            return false;
        }

        lastAppliedPostTerminalActionKey = actionKey;
        autoRegeneratingAfterDisconnect = true;
        try
        {
            RestartWaitingSession(
                preserveHelperIdentityForRetry: ShouldPreserveHelperIdentityForPostTerminalAction(flow),
                preservePeerEndedNotice: flow.ShouldShowPeerEndedNotice,
                preservedPeerEndedText: flow.TerminalStatusText);
            return true;
        }
        finally
        {
            autoRegeneratingAfterDisconnect = false;
        }
    }

    private bool TryApplyAcceptedHelpRequestFailureReset(SessionFlowSnapshot flow)
    {
        if (!ShouldResetAfterAcceptedHelpRequestFailure(flow) ||
            autoRegeneratingAfterDisconnect ||
            IsStartupBlocked ||
            startupFailureBlocksAutoRestart ||
            disposed)
        {
            return false;
        }

        var actionKey = $"accepted_help_request_failed|{flow.RuntimeState}|{flow.LastEndOrigin}|{flow.FailureReason}|{flow.SessionId ?? "(none)"}";
        if (string.Equals(lastAppliedPostTerminalActionKey, actionKey, StringComparison.Ordinal))
        {
            return false;
        }

        lastAppliedPostTerminalActionKey = actionKey;
        autoRegeneratingAfterDisconnect = true;
        try
        {
            RestartWaitingSession(
                preserveHelperIdentityForRetry: false,
                preservePeerEndedNotice: false);
            return true;
        }
        finally
        {
            autoRegeneratingAfterDisconnect = false;
        }
    }

    private bool ShouldPreserveHelperIdentityForPostTerminalAction(SessionFlowSnapshot flow)
    {
        return flow.PostTerminalAction == SessionFlowPostTerminalAction.ReturnToWaitingPreserveBootstrap &&
               !ShouldResetAfterAcceptedHelpRequestFailure(flow);
    }

    private bool ShouldResetAfterAcceptedHelpRequestFailure(SessionFlowSnapshot flow)
    {
        return sessionRuntime.PendingOutboundHelpRequestDecision is { Accepted: true } &&
               flow.Role == SessionRuntimeRole.Helpee &&
               !flow.ApprovalActive &&
               flow.TerminalKind == SessionTerminalKind.None &&
               !string.Equals(flow.FailureReason, "session_liveness_timeout", StringComparison.Ordinal) &&
               flow.RuntimeState is SessionRuntimeState.Failed or SessionRuntimeState.Disconnected or SessionRuntimeState.Rejected &&
               flow.Phase is SessionFlowPhase.HelpeeWaiting or SessionFlowPhase.Failed or SessionFlowPhase.Ended &&
               flow.LastEndOrigin is SessionFlowEndOrigin.Remote or SessionFlowEndOrigin.Failed or SessionFlowEndOrigin.Rejected;
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

    private void OnHelpRequestDecisionAvailable(object? sender, EventArgs e)
    {
        if (disposed)
        {
            return;
        }

        var capturedDecision = sessionRuntime.PendingOutboundHelpRequestDecision;
        _ = UiThreadDispatch.RunAsync(() =>
        {
            var decision = capturedDecision ?? sessionRuntime.PendingOutboundHelpRequestDecision;
            if (decision is null)
            {
                UpdateUiFromSnapshot("help_request_decision_cleared");
                return;
            }

            if (decision.Accepted)
            {
                ClearFailurePresentation();
                ConnectionState = "Waiting";
                ConnectionStatus = "Helper accepted. Establishing secure session…";
            }
            else
            {
                var statusText = GetHelpRequestDecisionStatusText(decision.Reason);
                ConnectionState = "Waiting";
                ConnectionStatus = statusText;
                if (ShouldClearHelperIdentityAfterHelpRequestDecision(decision.Reason))
                {
                    ClearInviteHelperIdentityAfterUnavailableHelper(statusText);
                }
                else
                {
                    UpdateShareInviteStatusText(statusText);
                }
            }

            UpdateUiFromSnapshot("help_request_decision");
        });
    }

    private void ClearInviteHelperIdentityAfterUnavailableHelper(string statusText)
    {
        InviteHelperIdentityInput = string.Empty;
        SetVerifiedInviteHelperIdentity(null, refreshInvite: false);
        UpdateShareInviteStatusText(statusText);
    }

    private static bool ShouldClearHelperIdentityAfterHelpRequestDecision(string? reason)
    {
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? string.Empty : reason.Trim();
        return string.Equals(normalizedReason, "helper_closed", StringComparison.Ordinal) ||
               string.Equals(normalizedReason, "request_timeout", StringComparison.Ordinal);
    }

    private static string GetHelpRequestDecisionStatusText(string? reason)
    {
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? string.Empty : reason.Trim();
        return normalizedReason switch
        {
            "helper_closed" => "The helper is no longer available.",
            "request_timeout" => "The help request expired.",
            _ => "The helper declined the request.",
        };
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

    private void NotifyRemoteControlDiagnosticsChanged()
    {
        OnPropertyChanged(nameof(ShowRemoteControlDebugToggle));
        OnPropertyChanged(nameof(ShowRemoteControlDebugOverlay));
        OnPropertyChanged(nameof(RemoteControlDebugToggleText));
        OnPropertyChanged(nameof(RemoteControlDiagnosticsRoleText));
        OnPropertyChanged(nameof(RemoteControlDiagnosticsControlStateText));
        OnPropertyChanged(nameof(RemoteControlDiagnosticsControlModeText));
        OnPropertyChanged(nameof(RemoteControlDiagnosticsDisplayText));
        OnPropertyChanged(nameof(RemoteControlDiagnosticsCaptureFrameText));
        OnPropertyChanged(nameof(RemoteControlDiagnosticsMoveStatsText));
        OnPropertyChanged(nameof(RemoteControlDiagnosticsSuppressionsText));
        OnPropertyChanged(nameof(RemoteControlDiagnosticsLastMappedText));
        OnPropertyChanged(nameof(RemoteControlDebugRequestIdText));
        OnPropertyChanged(nameof(RemoteControlDebugControllerPeerText));
        OnPropertyChanged(nameof(RemoteControlDebugDisplayText));
        OnPropertyChanged(nameof(RemoteControlDebugInjectorText));
        OnPropertyChanged(nameof(RemoteControlDebugQueueText));
        OnPropertyChanged(nameof(RemoteControlDebugGuardrailCountersText));
        ToggleRemoteControlDebugPanelCommand.NotifyCanExecuteChanged();
    }

    private RemoteControlDebugSnapshot GetRemoteControlDiagnosticsSnapshot()
    {
#if DEBUG
        return RemoteControlDebugDiagnostics.Snapshot(RemoteControlDiagnosticsRole.Helpee);
#else
        return RemoteControlDebugSnapshot.Empty(RemoteControlDiagnosticsRole.Helpee);
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
        var sentPerSecond = snapshot.MouseMoveSentPerSec?.ToString(CultureInfo.InvariantCulture) ?? "n/a";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"sent={sentPerSecond}/s; dropped={snapshot.MouseMoveDropped}; clamps={snapshot.OutOfRangeClamps}");
    }

    private static string FormatSuppressionText(RemoteControlDebugSnapshot snapshot)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"suppressed={snapshot.SuppressedInjections}; flushes={snapshot.QueueFlushes}; last_injected_seq={snapshot.LastInjectedSeq}; last_ack_seq={snapshot.LastAckSentSeq}; ack_sent={snapshot.AckSentCount}; snap_rx={snapshot.SnapshotReceivedCount}; snap_applied={snapshot.SnapshotAppliedCount}; snap_unstuck_buttons={snapshot.SnapshotUnstuckButtonsCount}; snap_unstuck_mods={snapshot.SnapshotUnstuckModifiersCount}; snap_last_rx={snapshot.HelpeeLastSnapshotReceivedSeq}({snapshot.HelpeeLastSnapshotReceivedModifiersMask}/{snapshot.HelpeeLastSnapshotReceivedMouseButtonsMask}); snap_last_applied={snapshot.HelpeeLastSnapshotAppliedSeq}({snapshot.HelpeeLastSnapshotAppliedModifiersMask}/{snapshot.HelpeeLastSnapshotAppliedMouseButtonsMask})");
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
        bool nextCanStartOrConnect;
        bool nextCanEndSession;
        bool nextCanOpenDiagnostics;
        bool nextCanSendFiles;
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
        var suppressConnectedControlsDuringLocalEnd =
            localEndCommandInFlight ||
            suppressConnectedControlsAfterLocalEnd ||
            flow.SuppressConnectedControls;
        var connectedForChat = flow.CanUseChatControls;
        EffectivePhase = phase;
        nextCanEndSession = !suppressConnectedControlsDuringLocalEnd && CanEndForPhase(phase);

        if (!FeatureFlags.UsePhaseDrivenGating || uiStateStore is null)
        {
            nextCanStartOrConnect = phase is SessionUiPhase.Idle
                or SessionUiPhase.Waiting
                or SessionUiPhase.Recovering
                or SessionUiPhase.Failed
                or SessionUiPhase.Ended;
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
                    nextCanStartOrConnect = false;
                    nextCanOpenDiagnostics = openDiagnosticsAction is not null && flow.ShowDiagnosticsAction;
                    nextCanSendFiles = sessionRuntime.CanPerform(SessionCapability.FileTransfer) &&
                                       !hasActiveOutboundTransfer;
                    break;

                case SessionUiPhase.Connecting:
                    nextChatEnabled = connectedForChat;
                    nextCanStartOrConnect = false;
                    nextCanOpenDiagnostics = openDiagnosticsAction is not null && flow.ShowDiagnosticsAction;
                    nextCanSendFiles = false;
                    break;

                case SessionUiPhase.Failed:
                case SessionUiPhase.Ended:
                    nextChatEnabled = false;
                    nextCanStartOrConnect = !IsStartupBlocked;
                    nextCanOpenDiagnostics = openDiagnosticsAction is not null && flow.ShowDiagnosticsAction;
                    nextCanSendFiles = false;
                    break;

                case SessionUiPhase.Idle:
                case SessionUiPhase.Waiting:
                    nextChatEnabled = false;
                    nextCanStartOrConnect = !IsStartupBlocked;
                    nextCanOpenDiagnostics = false;
                    nextCanSendFiles = false;
                    break;

                default:
                    nextChatEnabled = false;
                    nextCanStartOrConnect = !IsStartupBlocked;
                    nextCanOpenDiagnostics = openDiagnosticsAction is not null && flow.ShowDiagnosticsAction;
                    nextCanSendFiles = false;
                    break;
            }
        }

        if (suppressConnectedControlsDuringLocalEnd)
        {
            nextChatEnabled = false;
            nextCanEndSession = false;
            nextCanSendFiles = false;
        }

        IsChatInputEnabled = nextChatEnabled;
        CanStartOrConnect = nextCanStartOrConnect;
        CanEndSession = nextCanEndSession;
        CanOpenDiagnostics = nextCanOpenDiagnostics;
        CanSendFiles = nextCanSendFiles;

        RefreshCommandStates();
        LogCurrentChatPanelState(source);
        AssertUiConsistency();
    }

    private void RefreshCommandStates()
    {
        OnPropertyChanged(nameof(CanApplyInviteHelperIdentityAction));
        OnPropertyChanged(nameof(CanClearInviteHelperIdentityAction));
        OnPropertyChanged(nameof(CanRequestHelpAction));
        OnPropertyChanged(nameof(CanAllowIncomingRequestAction));
        OnPropertyChanged(nameof(ShowRetryAction));
        OnPropertyChanged(nameof(ShowOpenDiagnosticsLink));
        OnPropertyChanged(nameof(ShowSendFileAction));
        OnPropertyChanged(nameof(CanSendFileAction));
        OnPropertyChanged(nameof(CanSubmitRemoteControlConsent));

        ApplyInviteHelperIdentityCommand.NotifyCanExecuteChanged();
        ClearInviteHelperIdentityCommand.NotifyCanExecuteChanged();
        RequestHelpCommand.NotifyCanExecuteChanged();
        AllowCommand.NotifyCanExecuteChanged();
        DeclineCommand.NotifyCanExecuteChanged();
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
        ToggleScreenSharePreviewCommand.NotifyCanExecuteChanged();
        RequestControlCommand.NotifyCanExecuteChanged();
        StopControlCommand.NotifyCanExecuteChanged();
        RestartAsAdministratorCommand.NotifyCanExecuteChanged();
        ToggleControlModeCommand.NotifyCanExecuteChanged();
        ToggleRemoteControlDebugPanelCommand.NotifyCanExecuteChanged();
        AllowControlConsentCommand.NotifyCanExecuteChanged();
        DenyControlConsentCommand.NotifyCanExecuteChanged();
    }

    private void LogCurrentChatPanelState(string source)
    {
        var fileTransferSnapshot = sessionRuntime.FileTransferSnapshot;
        var payload =
            $"event=helpee_chat_panel_state; source={source}; phase={EffectivePhase}; connection_state={ConnectionState}; " +
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
        LocalOperationalLog.Info("HelpeeUi", payload);
    }

    private void LogFileTransferActionUi(
        string eventName,
        string? normalizedTransferId,
        bool includeDecline)
    {
        var inbound = InboundFileTransfer;
        var payload =
            $"event={eventName}; role=Helpee; transfer_id_present={(normalizedTransferId is null ? 0 : 1)}; " +
            $"inbound_state={inbound?.State.ToString() ?? "(none)"}; " +
            $"inbound_show_accept={(inbound?.ShowAccept == true ? 1 : 0)}; " +
            $"effective_phase={EffectivePhase}; runtime_state={sessionRuntime.State}";
        if (includeDecline)
        {
            payload += $"; inbound_show_decline={(inbound?.ShowDecline == true ? 1 : 0)}";
        }

        LocalOperationalLog.Info("HelpeeUi", payload);
    }

    private void LogSendFileActionUi(string eventName, string reason)
    {
        LocalOperationalLog.Info(
            "HelpeeUi",
            $"event={eventName}; role=Helpee; reason={reason}; " +
            $"can_send_files={(CanSendFiles ? 1 : 0)}; can_send_file_action={(CanExecuteSendFileAction() ? 1 : 0)}; " +
            $"effective_phase={EffectivePhase}; runtime_state={sessionRuntime.State}");
    }

    private void LogFileTransferCancelUi(string eventName, string? normalizedTransferId)
    {
        var inbound = InboundFileTransfer;
        var outbound = OutboundFileTransfer;
        LocalOperationalLog.Info(
            "HelpeeUi",
            $"event={eventName}; role=Helpee; transfer_id_present={(normalizedTransferId is null ? 0 : 1)}; " +
            $"inbound_state={inbound?.State.ToString() ?? "(none)"}; inbound_show_cancel={(inbound?.ShowCancel == true ? 1 : 0)}; " +
            $"outbound_state={outbound?.State.ToString() ?? "(none)"}; outbound_show_cancel={(outbound?.ShowCancel == true ? 1 : 0)}; " +
            $"effective_phase={EffectivePhase}; runtime_state={sessionRuntime.State}");
    }

    private void LogFileTransferPauseResumeUi(string eventName, string? normalizedTransferId)
    {
        var inbound = InboundFileTransfer;
        var outbound = OutboundFileTransfer;
        LocalOperationalLog.Info(
            "HelpeeUi",
            $"event={eventName}; role=Helpee; transfer_id_present={(normalizedTransferId is null ? 0 : 1)}; " +
            $"inbound_state={inbound?.State.ToString() ?? "(none)"}; inbound_show_pause={(inbound?.ShowPause == true ? 1 : 0)}; inbound_show_resume={(inbound?.ShowResume == true ? 1 : 0)}; " +
            $"outbound_state={outbound?.State.ToString() ?? "(none)"}; outbound_show_pause={(outbound?.ShowPause == true ? 1 : 0)}; outbound_show_resume={(outbound?.ShowResume == true ? 1 : 0)}; " +
            $"effective_phase={EffectivePhase}; runtime_state={sessionRuntime.State}");
    }

    private static string SanitizeForLog(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "(none)"
            : value.Replace(';', ',').Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    private void SyncIncomingApprovalRequestFromRuntime()
    {
        var approvalRequest = sessionRuntime.PendingApprovalRequest;
        if (approvalRequest is null)
        {
            ResetIncomingApprovalRequestState();
            return;
        }

        var helperIdentity = approvalRequest.HelperIdentity.Value;
        var sessionId = approvalRequest.SessionId.Value;
        var requestedCapabilities = approvalRequest.RequestedCapabilities;
        var selectionKey = string.Create(
            CultureInfo.InvariantCulture,
            $"{sessionId}|{helperIdentity}|{(int)requestedCapabilities}");

        var selectionChanged = !string.Equals(incomingApprovalSelectionKey, selectionKey, StringComparison.Ordinal);
        incomingApprovalSelectionKey = selectionKey;
        UpdateIncomingApprovalMetadata(helperIdentity, sessionId, requestedCapabilities);
        if (selectionChanged)
        {
            SetIncomingCapabilitySelections(requestedCapabilities);
        }
    }

    private void ResetIncomingApprovalRequestState()
    {
        incomingApprovalSelectionKey = string.Empty;
        UpdateIncomingApprovalMetadata(string.Empty, string.Empty, CapabilityGrant.None);
        SetIncomingCapabilitySelections(CapabilityGrant.None);
    }

    private void UpdateIncomingApprovalMetadata(string helperIdentity, string sessionId, CapabilityGrant requestedCapabilities)
    {
        if (!string.IsNullOrWhiteSpace(verifiedInviteVerificationIdentity) &&
            !string.IsNullOrWhiteSpace(helperIdentity) &&
            !string.Equals(verifiedInviteVerificationIdentity, helperIdentity, StringComparison.Ordinal))
        {
            AppLog.Warn(
                $"Helpee verification identity mismatch; preview={verifiedInviteVerificationIdentity}; approval={helperIdentity}. Clearing preview verification identity.");
            verifiedInviteVerificationIdentity = string.Empty;
            OnPropertyChanged(nameof(VerifiedInviteHelperVerificationCode));
            OnPropertyChanged(nameof(HasVerifiedInviteHelperVerificationCode));
            OnPropertyChanged(nameof(HeaderVerificationCodeText));
            OnPropertyChanged(nameof(ShowHeaderVerificationCode));
            OnPropertyChanged(nameof(FirstPillVerificationCodeText));
            OnPropertyChanged(nameof(ShowFirstPillVerificationCode));
        }

        if (SetProperty(ref incomingHelperIdentity, helperIdentity ?? string.Empty, nameof(IncomingHelperIdentityText)))
        {
            OnPropertyChanged(nameof(IncomingHelperName));
            OnPropertyChanged(nameof(IncomingHelperVerificationCode));
            OnPropertyChanged(nameof(HasIncomingHelperVerificationCode));
            OnPropertyChanged(nameof(HeaderVerificationCodeText));
            OnPropertyChanged(nameof(ShowHeaderVerificationCode));
            OnPropertyChanged(nameof(FirstPillVerificationCodeText));
            OnPropertyChanged(nameof(ShowFirstPillVerificationCode));
            NotifySessionVerificationPropertiesChanged();
            OnPropertyChanged(nameof(IncomingTechnicalHelperIdentityText));
            OnPropertyChanged(nameof(HasIncomingTechnicalHelperIdentity));
            OnPropertyChanged(nameof(HasIncomingTechnicalDetails));
        }

        if (SetProperty(ref incomingSessionId, sessionId ?? string.Empty, nameof(IncomingSessionIdText)))
        {
            OnPropertyChanged(nameof(IncomingTechnicalSessionIdText));
            OnPropertyChanged(nameof(HasIncomingTechnicalSessionId));
            OnPropertyChanged(nameof(HasIncomingTechnicalDetails));
        }

        if (SetProperty(ref incomingRequestedCapabilities, requestedCapabilities, nameof(IncomingRequestedCapabilitiesText)))
        {
            OnPropertyChanged(nameof(ShowIncomingRequestedCapabilities));
            OnPropertyChanged(nameof(ShowIncomingChatCapability));
            OnPropertyChanged(nameof(ShowIncomingScreenShareCapability));
            OnPropertyChanged(nameof(ShowIncomingRemoteControlCapability));
            OnPropertyChanged(nameof(CanAllowIncomingRemoteControlCapability));
            OnPropertyChanged(nameof(ShowIncomingFileTransferCapability));
            OnPropertyChanged(nameof(ShowIncomingClipboardCapability));
        }
    }

    private void SetIncomingCapabilitySelections(CapabilityGrant approvedCapabilities)
    {
        SetIncomingCapabilitySelectionCore(ref allowIncomingChatCapability, (approvedCapabilities & CapabilityGrant.Chat) == CapabilityGrant.Chat, nameof(AllowIncomingChatCapability));
        SetIncomingCapabilitySelectionCore(ref allowIncomingScreenShareCapability, (approvedCapabilities & CapabilityGrant.ScreenShare) == CapabilityGrant.ScreenShare, nameof(AllowIncomingScreenShareCapability));
        SetIncomingCapabilitySelectionCore(ref allowIncomingRemoteControlCapability, (approvedCapabilities & CapabilityGrant.RemoteControl) == CapabilityGrant.RemoteControl, nameof(AllowIncomingRemoteControlCapability));
        SetIncomingCapabilitySelectionCore(ref allowIncomingFileTransferCapability, (approvedCapabilities & CapabilityGrant.FileTransfer) == CapabilityGrant.FileTransfer, nameof(AllowIncomingFileTransferCapability));
        SetIncomingCapabilitySelectionCore(ref allowIncomingClipboardCapability, (approvedCapabilities & CapabilityGrant.Clipboard) == CapabilityGrant.Clipboard, nameof(AllowIncomingClipboardCapability));
        NormalizeIncomingCapabilityDependencies();
        OnIncomingCapabilitySelectionChanged();
    }

    private void NotifySessionVerificationPropertiesChanged()
    {
        OnPropertyChanged(nameof(SessionVerificationEmojiSequence));
        OnPropertyChanged(nameof(SessionVerificationFallbackCode));
        OnPropertyChanged(nameof(HasSessionVerificationCode));
        OnPropertyChanged(nameof(ShowSessionVerificationCode));
    }

    private void SetIncomingCapabilitySelection(ref bool field, bool value, string propertyName)
    {
        SetIncomingCapabilitySelectionCore(ref field, value, propertyName);
        NormalizeIncomingCapabilityDependencies();
        OnIncomingCapabilitySelectionChanged();
    }

    private void SetIncomingCapabilitySelectionCore(ref bool field, bool value, string propertyName)
    {
        SetProperty(ref field, value, propertyName);
    }

    private void NormalizeIncomingCapabilityDependencies()
    {
        if (!allowIncomingScreenShareCapability && allowIncomingRemoteControlCapability)
        {
            SetIncomingCapabilitySelectionCore(ref allowIncomingRemoteControlCapability, false, nameof(AllowIncomingRemoteControlCapability));
        }

        OnPropertyChanged(nameof(CanAllowIncomingRemoteControlCapability));
    }

    private void OnIncomingCapabilitySelectionChanged()
    {
        OnPropertyChanged(nameof(IncomingApprovedCapabilitiesText));
        OnPropertyChanged(nameof(CanAllowIncomingRequestAction));
        AllowCommand.NotifyCanExecuteChanged();
    }

    private CapabilityGrant GetSelectedIncomingApprovalCapabilities()
    {
        var selected = CapabilityGrant.None;

        if (allowIncomingChatCapability)
        {
            selected |= CapabilityGrant.Chat;
        }

        if (allowIncomingScreenShareCapability)
        {
            selected |= CapabilityGrant.ScreenShare;
        }

        if (allowIncomingRemoteControlCapability)
        {
            selected |= CapabilityGrant.RemoteControl;
        }

        if (allowIncomingFileTransferCapability)
        {
            selected |= CapabilityGrant.FileTransfer;
        }

        if (allowIncomingClipboardCapability)
        {
            selected |= CapabilityGrant.Clipboard;
        }

        selected &= incomingRequestedCapabilities;

        if ((selected & CapabilityGrant.ScreenShare) != CapabilityGrant.ScreenShare)
        {
            selected &= ~CapabilityGrant.RemoteControl;
        }

        return selected;
    }

    private static string BuildCapabilitySummary(CapabilityGrant capabilities)
    {
        if (capabilities == CapabilityGrant.None)
        {
            return "None";
        }

        var names = new List<string>(5);
        if ((capabilities & CapabilityGrant.Chat) == CapabilityGrant.Chat)
        {
            names.Add("Chat");
        }

        if ((capabilities & CapabilityGrant.ScreenShare) == CapabilityGrant.ScreenShare)
        {
            names.Add("Screen view");
        }

        if ((capabilities & CapabilityGrant.RemoteControl) == CapabilityGrant.RemoteControl)
        {
            names.Add("Remote control");
        }

        if ((capabilities & CapabilityGrant.FileTransfer) == CapabilityGrant.FileTransfer)
        {
            names.Add("File transfer");
        }

        if ((capabilities & CapabilityGrant.Clipboard) == CapabilityGrant.Clipboard)
        {
            names.Add("Clipboard");
        }

        return string.Join(", ", names);
    }

    private static bool CanEndForPhase(SessionUiPhase phase) =>
        phase is SessionUiPhase.Connecting
            or SessionUiPhase.Connected
            or SessionUiPhase.Recovering;

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
            !IsConnectedView)
        {
            throw new InvalidOperationException("Helpee UI invariant failed: Connected phase requires ConnectionState=Connected.");
        }

        if (uiStateStore is not null && uiStateStore.Phase == SessionUiPhase.Failed && IsChatInputEnabled)
        {
            throw new InvalidOperationException("Helpee UI invariant failed: Failed phase requires disabled chat input.");
        }

        if (IsChatInputEnabled &&
            uiStateStore?.Phase != SessionUiPhase.Connected &&
            !sessionRuntime.FlowSnapshot.IsConnectedShellVisible)
        {
            throw new InvalidOperationException("Helpee UI invariant failed: chat input requires connected phase or runtime state.");
        }

        if ((localEndCommandInFlight || suppressConnectedControlsAfterLocalEnd) && ShowTransientBanner)
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

        if (!localEndCommandInFlight &&
            !suppressConnectedControlsAfterLocalEnd &&
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

        incomingRequestExpiresAtUtc = DateTimeOffset.UtcNow + incomingRequestTimeout;
        UpdateIncomingRequestTimeoutText(BuildIncomingRequestTimeoutText(DateTimeOffset.UtcNow));
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
                await sessionRuntime.RejectAsync("approval_timeout", CancellationToken.None).ConfigureAwait(false);
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
                RestartWaitingSession(
                    preserveHelperIdentityForRetry: true,
                    preservePeerEndedNotice: false);
            });
        });
    }

    private void CancelIncomingRequestTimeout()
    {
        incomingRequestTimeoutCts?.Cancel();
        incomingRequestTimeoutCts?.Dispose();
        incomingRequestTimeoutCts = null;
        incomingRequestExpiresAtUtc = DateTimeOffset.MinValue;
        UpdateIncomingRequestTimeoutText(string.Empty);
    }

    private void PrepareForNewSession()
    {
        RequestStopScreenSharePreview("prepare_new_session");

        if (!localEndCommandInFlight)
        {
            return;
        }

        localEndCommandInFlight = false;
        lastPeerEndedNoticeKey = string.Empty;
        lastAppliedPostTerminalActionKey = string.Empty;
        ClearSessionConversationUi();
        presenterBannerStatus = UserFacingStatus.IdleStatus;
        BannerStatus = presenterBannerStatus;
        if (uiStateStore is not null)
        {
            uiStateStore.SetPhase(SessionUiPhase.Waiting, "StartNewSession:Helpee");
            ApplySessionBannerPolicy();
        }
    }

    private void ClearSessionConversationUi()
    {
        ChatDraft = string.Empty;
        ChatMessages.Clear();
        InboundFileTransfer = null;
        OutboundFileTransfer = null;
        OnPropertyChanged(nameof(HasChatMessages));
        OnPropertyChanged(nameof(ShowNoMessagesPlaceholder));
    }

    private void RequestStopScreenSharePreview(string reason)
    {
        if (Interlocked.Exchange(ref screenSharePreviewStopInFlight, 1) == 1)
        {
            return;
        }

        _ = StopScreenSharePreviewAsync(reason);
    }

    private static bool IsProtectedSeedStorageReadFailure(string? message)
    {
        return !string.IsNullOrWhiteSpace(message) &&
               message.Contains("Protected seed storage could not be read.", StringComparison.OrdinalIgnoreCase);
    }

    private void PromoteProtectedSeedStorageStartupFailureIfNeeded()
    {
        if (!IsProtectedSeedStorageReadFailure(sessionRuntime.StatusText))
        {
            return;
        }

        startupFailureBlocksAutoRestart = true;
        autoRegeneratingAfterDisconnect = false;
        UpdateShareInviteStatusText(sessionRuntime.StatusText);
    }

    private async Task StopScreenSharePreviewAsync(string reason)
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
            ForceCloseWindowsGraphicsCaptureLeases($"preview_stop:{reason}");
        }
    }

    private void StopLocalScreenSharePreviewUiImmediately(string reason)
    {
        ApplyImmediateScreenSharePreviewStopState(reason);
        RequestTransportScreenShareSync(false, reason);
        RequestStopScreenSharePreview(reason);
    }

    private static void ForceCloseWindowsGraphicsCaptureLeases(string reason)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var safeReason = SensitiveDataRedactor.Redact(
            string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason.Trim());
        try
        {
            WindowsGraphicsCaptureRawSource.ForceCloseAllScreenShareLeases(safeReason);
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Info(
                "HelpeeUi",
                $"event=screenshare_wgc_force_close_all_failed; reason={safeReason}; ex={ex.GetType().Name}");
        }
    }

    private void ApplyImmediateScreenSharePreviewStopState(string reason)
    {
        var shouldLogPreviewHidden =
            IsScreenSharingPreviewActive ||
            ScreenSharePreviewFrame is not null ||
            ScreenSharePreviewStatus.State != ScreenShareState.Off ||
            helpeePreviewSurfaceVisibleLogged;
        var previousFrame = ScreenSharePreviewFrame;
        ScreenSharePreviewFrame = null;
        try
        {
            previousFrame?.Dispose();
        }
        catch
        {
            // Best-effort preview teardown.
        }
        IsScreenSharingPreviewActive = false;
        ScreenSharePreviewStatus = new ScreenShareStatus(ScreenShareState.Off, null, DateTimeOffset.UtcNow);
        if (shouldLogPreviewHidden)
        {
            LocalOperationalLog.Info(
                "HelpeeUi",
                $"event=helpee_screenshare_preview_surface_hidden; role=helpee_preview; reason={SanitizeForLog(reason)}; header_status={SanitizeForLog(HeaderStatusText)}; preview_status={ScreenSharePreviewStatus.State}");
        }
    }

    private void RequestTransportScreenShareSync(bool isPreviewActive, string trigger)
    {
        desiredTransportScreenSharePreviewActive = isPreviewActive;
        desiredTransportScreenShareSyncTrigger = string.IsNullOrWhiteSpace(trigger)
            ? "unknown"
            : trigger.Trim();
        Interlocked.Exchange(ref transportScreenShareSyncQueued, 1);
        if (Interlocked.CompareExchange(ref transportScreenShareSyncLoopActive, 1, 0) != 0)
        {
            return;
        }

        _ = RunTransportScreenShareSyncLoopAsync();
    }

    private async Task RunTransportScreenShareSyncLoopAsync()
    {
        try
        {
            while (!disposed &&
                   Interlocked.Exchange(ref transportScreenShareSyncQueued, 0) == 1)
            {
                var desiredPreviewState = desiredTransportScreenSharePreviewActive;
                var trigger = desiredTransportScreenShareSyncTrigger;
                await SyncTransportScreenShareWithPreviewAsync(desiredPreviewState, trigger).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Exchange(ref transportScreenShareSyncLoopActive, 0);
            if (!disposed &&
                Volatile.Read(ref transportScreenShareSyncQueued) == 1 &&
                Interlocked.CompareExchange(ref transportScreenShareSyncLoopActive, 1, 0) == 0)
            {
                _ = RunTransportScreenShareSyncLoopAsync();
            }
        }
    }

    private bool ShouldRetryTransportScreenShareStart()
    {
        return !disposed &&
               IsScreenSharingPreviewActive &&
               sessionRuntime.Role == SessionRuntimeRole.Helpee &&
               sessionRuntime.State == SessionRuntimeState.Connected &&
               !sessionRuntime.IsTransportScreenShareActive;
    }

    private async Task SyncTransportScreenShareWithPreviewAsync(bool isPreviewActive, string trigger)
    {
        if (disposed ||
            !FeatureFlags.EnableScreenShareTransport ||
            !FeatureFlags.EnableScreenShareCapture)
        {
            return;
        }

        try
        {
            if (isPreviewActive)
            {
                await sessionRuntime.StartTransportScreenShareAsync().ConfigureAwait(false);
                if (ShouldRetryTransportScreenShareStart())
                {
                    LocalOperationalLog.Info(
                        "HelpeeUi",
                        $"event=helpee_transport_screenshare_retry_scheduled; trigger={SanitizeForLog(trigger)}; reason=transport_not_active_after_start; runtime_state={sessionRuntime.State}; role={sessionRuntime.Role}; transport_active={(sessionRuntime.IsTransportScreenShareActive ? 1 : 0)}");
                    await Task.Delay(TransportScreenShareRetryDelay).ConfigureAwait(false);
                    if (ShouldRetryTransportScreenShareStart())
                    {
                        await sessionRuntime.StartTransportScreenShareAsync().ConfigureAwait(false);
                    }
                }
            }
            else
            {
                if (ShouldDeferTransportPreviewTeardownToSessionDisconnect())
                {
                    return;
                }

                if (sessionRuntime.ControlState == ControlState.Active)
                {
                    await sessionRuntime.StopRemoteControlAsync(
                            "screenshare_stopped_local",
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                else if (sessionRuntime.ControlState == ControlState.Requesting ||
                         sessionRuntime.HasPendingRemoteControlConsentPrompt)
                {
                    await sessionRuntime.StopRemoteControlAsync(
                            "screenshare_stopped_pending_request",
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }

                await sessionRuntime.StopTransportScreenShareAsync("preview_stopped").ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn(
                "HelpeeUi",
                $"event=helpee_transport_screenshare_sync_failed; trigger={SanitizeForLog(trigger)}; preview_active={(isPreviewActive ? 1 : 0)}; runtime_state={sessionRuntime.State}; role={sessionRuntime.Role}; transport_active={(sessionRuntime.IsTransportScreenShareActive ? 1 : 0)}; ex={SanitizeForLog(ex.GetType().Name)}; message={SanitizeForLog(ex.Message)}");
            if (isPreviewActive)
            {
                await Task.Delay(TransportScreenShareRetryDelay).ConfigureAwait(false);
                if (ShouldRetryTransportScreenShareStart())
                {
                    try
                    {
                        LocalOperationalLog.Info(
                            "HelpeeUi",
                            $"event=helpee_transport_screenshare_retry_started; trigger={SanitizeForLog(trigger)}; runtime_state={sessionRuntime.State}; role={sessionRuntime.Role}");
                        await sessionRuntime.StartTransportScreenShareAsync().ConfigureAwait(false);
                    }
                    catch (Exception retryEx)
                    {
                        LocalOperationalLog.Warn(
                            "HelpeeUi",
                            $"event=helpee_transport_screenshare_retry_failed; trigger={SanitizeForLog(trigger)}; preview_active=1; runtime_state={sessionRuntime.State}; role={sessionRuntime.Role}; transport_active={(sessionRuntime.IsTransportScreenShareActive ? 1 : 0)}; ex={SanitizeForLog(retryEx.GetType().Name)}; message={SanitizeForLog(retryEx.Message)}");
                    }
                }
            }
        }
    }

    private bool ShouldDeferTransportPreviewTeardownToSessionDisconnect()
    {
        return localEndCommandInFlight || Volatile.Read(ref windowCloseDisconnectStarted) != 0;
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
        string baseText;
        if (TryGetRemoteControlHeaderHint(out var remoteControlHint))
        {
            baseText = remoteControlHint;
        }
        else if (TryGetConnectedHeaderStatusText(out var connectedStatusText))
        {
            baseText = connectedStatusText;
        }
        else if (IsIncomingRequestView || HasIncomingRequest || sessionRuntime.FlowSnapshot.ShowIncomingApproval)
        {
            baseText = "Waiting for your approval…";
        }
        else if (showPeerEndedNotice && !string.IsNullOrWhiteSpace(peerEndedNoticeText))
        {
            baseText = peerEndedNoticeText;
        }
        else
        {
            baseText = EffectivePhase switch
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
        }

        if (ScreenSharePreviewStatus.State == ScreenShareState.Failed &&
            !string.IsNullOrWhiteSpace(ScreenShareViewerMessage) &&
            EffectivePhase is not (SessionUiPhase.Failed or SessionUiPhase.Ended))
        {
            return $"{baseText} • {ScreenShareViewerMessage}";
        }

        return AppendScreenShareSuffix(baseText);
    }

    private static HelpeeConnectionViewState MapConnectionViewState(string? state)
    {
        return state switch
        {
            "IncomingRequest" => HelpeeConnectionViewState.IncomingRequest,
            "Connected" => HelpeeConnectionViewState.Connected,
            "Disconnected" => HelpeeConnectionViewState.Disconnected,
            "Failed" => HelpeeConnectionViewState.Failed,
            _ => HelpeeConnectionViewState.Waiting,
        };
    }

    private bool TryGetConnectedHeaderStatusText(out string statusText)
    {
        statusText = string.Empty;
        if (!IsConnectedView)
        {
            return false;
        }

        if (IsScreenSharingPreviewActive && sessionRuntime.HasPendingRemoteControlConsentPrompt)
        {
            statusText = "Waiting for your approval…";
            return true;
        }

        if (sessionRuntime.ControlState == ControlState.Active &&
            !sessionRuntime.RemoteControlMappingAvailable)
        {
            statusText = "Waiting for fresh mapping";
            return true;
        }

        if (!string.IsNullOrWhiteSpace(sessionRuntime.RemoteControlStatusHintText))
        {
            statusText = sessionRuntime.RemoteControlStatusHintText;
            return true;
        }

        return false;
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

#if DEBUG
    private static string FormatLatency(DebugLatencySummary summary)
    {
        return !summary.HasSamples
            ? "na"
            : $"avg={summary.AverageMilliseconds:F1}ms p50={summary.P50Milliseconds:F1}ms p95={summary.P95Milliseconds:F1}ms n={summary.Count}";
    }
#endif

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
