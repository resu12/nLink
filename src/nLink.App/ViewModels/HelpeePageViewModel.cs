using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using NLink.Core.RemoteControl;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
#if DEBUG
using NLink.Core.Diagnostics;
#endif
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

    private static readonly TimeSpan DefaultIncomingRequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RecoveryTransientThrottle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultInviteLifetime = TimeSpan.FromMinutes(15);
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
    private string lastChatPanelStateLog = string.Empty;
    private long chatSendAttemptCounter;
    private SessionReliabilityAttempt? reliabilityAttempt;
    private readonly InlineTransientText copyFeedback = new();
    private string shareInviteText = string.Empty;
    private string shareInviteRawTokenText = string.Empty;
    private string shareAddressText = string.Empty;
    private string shareInviteStatusText = "Preparing invite…";
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
    private bool suppressAutoApplyInviteHelperIdentityInput;
    private string incomingHelperIdentity = string.Empty;
    private string incomingSessionId = string.Empty;
    private CapabilityGrant incomingRequestedCapabilities;
    private string incomingApprovalSelectionKey = string.Empty;
    private bool allowIncomingChatCapability;
    private bool allowIncomingScreenShareCapability;
    private bool allowIncomingRemoteControlCapability;
    private bool allowIncomingFileTransferCapability;
    private bool allowIncomingClipboardCapability;
    private readonly DispatcherTimer shareInviteExpiryTimer;
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
    private bool canSendFiles;
    private FileTransferPanelItemViewModel? inboundFileTransfer;
    private FileTransferPanelItemViewModel? outboundFileTransfer;
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
        inviteTokenFactory = ConnectInputResolverFactory.CreateInviteTokenFactory();
        this.incomingRequestTimeout = incomingRequestTimeout ?? DefaultIncomingRequestTimeout;
        this.uiStateStore = uiStateStore;
        shareInviteExpiryTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        shareInviteExpiryTimer.Tick += OnShareInviteExpiryTimerTick;
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
        sessionRuntime.SessionSecurityStateChanged += OnSessionSecurityStateChanged;
        sessionRuntime.TransientStatusChanged += OnTransientStatusChanged;
        sessionRuntime.IncomingJoinRequestAvailable += OnIncomingJoinRequestAvailable;
        sessionRuntime.Disconnected += OnRuntimeDisconnected;
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
        ApplySessionBannerPolicy();
        UpdateUiFromSnapshot();
    }

    public string ShareInvite => shareInviteText;
    public string ShareInviteRawToken => shareInviteRawTokenText;
    public string ShareAddress => shareAddressText;
    public string ShareInviteStatusText => shareInviteStatusText;
    public Bitmap? ShareInviteQrImage => shareInviteQrBitmap;
    public bool ShowShareInviteQr => ShareInviteQrImage is not null;
    public bool ShowShareInviteQrPlaceholder => !ShowShareInviteQr;
    public bool ShowShareInviteStatus => !HasShareInvite && !string.IsNullOrWhiteSpace(ShareInviteStatusText);
    public string ShareInviteExpiryText => shareInviteExpiryText;
    public bool ShowShareInviteExpiry => HasShareInvite && !string.IsNullOrWhiteSpace(ShareInviteExpiryText);
    public string IncomingRequestTimeoutText => incomingRequestTimeoutText;
    public bool ShowIncomingRequestTimeout =>
        ShowIncomingRequestPanel &&
        HasIncomingRequest &&
        !string.IsNullOrWhiteSpace(IncomingRequestTimeoutText);
    public bool HasShareInvite => !string.IsNullOrWhiteSpace(ShareInvite);
    public bool HasShareInviteRawToken =>
        !string.IsNullOrWhiteSpace(ShareInviteRawToken) &&
        !string.Equals(ShareInviteRawToken, ShareInvite, StringComparison.Ordinal);
    public bool HasShareAddress => !string.IsNullOrWhiteSpace(ShareAddress);
    public bool ShowInviteHelperIdentityPanel => ShowWaitingPanel && !IsUnboundPublicInviteFlowAvailable;
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
                ApplyInviteHelperIdentityCommand.NotifyCanExecuteChanged();
                ClearInviteHelperIdentityCommand.NotifyCanExecuteChanged();
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
                TryResolveInviteHelperIdentityInput(out var resolvedHelperIdentity, out _) &&
                string.Equals(resolvedHelperIdentity.Value, verifiedInviteHelperIdentity, StringComparison.Ordinal))
            {
                return "Invite will only work for this helper.";
            }

            if (string.IsNullOrWhiteSpace(normalizedInput))
            {
                return HasVerifiedInviteHelperIdentity
                    ? "Paste a different helper address to refresh the invite binding."
                    : "Paste the helper address your helper shared with you.";
            }

            if (!TryResolveInviteHelperIdentityInput(out _, out _))
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
        HelperVerificationCodeFormatter.FormatOrNull(verifiedInviteHelperIdentity) ?? string.Empty;
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
                OnPropertyChanged(nameof(ShowRemoteControlConsentDialog));
                StopControlCommand.NotifyCanExecuteChanged();
                AllowControlConsentCommand.NotifyCanExecuteChanged();
                DenyControlConsentCommand.NotifyCanExecuteChanged();
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
                OnPropertyChanged(nameof(ShowRemoteControlConsentDialog));
            }
        }
    }

    public bool IsChatReady => sessionRuntime.CanSendChat;

    private bool IsRemoteControlUiConnected =>
        EffectivePhase == SessionUiPhase.Connected &&
        sessionRuntime.State == SessionRuntimeState.Connected;

    public bool CanStartOrConnect
    {
        get => canStartOrConnect;
        private set
        {
            if (SetProperty(ref canStartOrConnect, value))
            {
                OnPropertyChanged(nameof(CanStartConnect));
                OnPropertyChanged(nameof(CanApplyInviteHelperIdentityAction));
                OnPropertyChanged(nameof(CanClearInviteHelperIdentityAction));
                ApplyInviteHelperIdentityCommand.NotifyCanExecuteChanged();
                ClearInviteHelperIdentityCommand.NotifyCanExecuteChanged();
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

    public bool IsScreenSharingPreviewActive
    {
        get => isScreenSharingPreviewActive;
        private set
        {
            if (SetProperty(ref isScreenSharingPreviewActive, value))
            {
                OnPropertyChanged(nameof(HeaderStatusText));
                OnPropertyChanged(nameof(ShowTransientStatusPanel));
                ToggleScreenSharePreviewCommand.NotifyCanExecuteChanged();
#if DEBUG
                UpdatePreviewSnapshotTimer();
#endif
                _ = SyncTransportScreenShareWithPreviewAsync(value);
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

    public RelayCommand AllowCommand { get; }

    public IAsyncRelayCommand DeclineCommand { get; }

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

    public bool ShowRetryAction => !IsStartupBlocked && connectionViewState == HelpeeConnectionViewState.Failed;
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
        shareInviteQrRefreshCts?.Cancel();
        shareInviteQrRefreshCts?.Dispose();
        shareInviteQrRefreshCts = null;
        shareInviteQrBitmap?.Dispose();
        shareInviteQrBitmap = null;

        sessionRuntime.StateChanged -= OnSessionRuntimeStateChanged;
        sessionRuntime.SessionSecurityStateChanged -= OnSessionSecurityStateChanged;
        sessionRuntime.TransientStatusChanged -= OnTransientStatusChanged;
        sessionRuntime.IncomingJoinRequestAvailable -= OnIncomingJoinRequestAvailable;
        sessionRuntime.Disconnected -= OnRuntimeDisconnected;
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

    private void RestartWaitingSession()
    {
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
        EnsureInviteSnapshot(forceNewToken: true);
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

        return IsScreenSharingPreviewActive ||
               (CanShowScreenShareAction && sessionRuntime.CanPerform(SessionCapability.ScreenShare));
    }

    private async Task RetryAsync()
    {
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

        StartHosting();
        EnsureInviteSnapshot(forceNewToken: true);
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
        if (!CanRespondToControlConsent())
        {
            return;
        }

        await sessionRuntime.RespondToRemoteControlRequestAsync(allow: true, CancellationToken.None);
    }

    private async Task DenyControlConsentAsync()
    {
        if (!CanRespondToControlConsent())
        {
            return;
        }

        await sessionRuntime.RespondToRemoteControlRequestAsync(allow: false, CancellationToken.None);
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
               sessionRuntime.ControlState == ControlState.Requesting &&
               sessionRuntime.Role == SessionRuntimeRole.Helpee;
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
        if (!RestartWaitingSessionAfterTerminalSession())
        {
            ConnectionStatus = "Waiting for helper…";
            ConnectionState = "Waiting";
        }
    }

    private void StartHosting()
    {
        PrepareForNewSession();
        EnsureInviteSnapshot(forceNewToken: false);

        if (IsStartupBlocked)
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
                    ConnectionStatus = "Could not start. Refresh invite and try again.";
                    ConnectionState = "Disconnected";
                });
            }
        }
    }

    private void EnsureInviteSnapshot(bool forceNewToken)
    {
        var candidateAddress = ResolveInviteAddress();
        var boundHelperAddress = ResolveVerifiedInviteHelperAddress();
        var boundHelperIdentity = boundHelperAddress?.Value ?? string.Empty;
        if (!PeerAddress.TryParse(candidateAddress, out var peerAddress))
        {
            UpdateShareAddressText(string.Empty);
            UpdateShareInviteText(string.Empty);
            UpdateShareInviteRawTokenText(string.Empty);
            UpdateShareInviteStatusText("Preparing invite…");
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
        return sessionRuntime.CurrentInvitePeerAddress?.Value ??
               sessionRuntime.CurrentLocalPeerAddress?.Value;
    }

    internal void SetVerifiedInviteHelperIdentity(
        PeerAddress? helperIdentity,
        bool refreshInvite = true,
        string? normalizedInputOverride = null)
    {
        var normalized = helperIdentity?.Value ?? string.Empty;
        suppressAutoApplyInviteHelperIdentityInput = true;
        try
        {
            InviteHelperIdentityInput = normalizedInputOverride ?? normalized;
        }
        finally
        {
            suppressAutoApplyInviteHelperIdentityInput = false;
        }

        if (string.Equals(verifiedInviteHelperIdentity, normalized, StringComparison.Ordinal))
        {
            return;
        }

        verifiedInviteHelperIdentity = normalized;
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
        OnPropertyChanged(nameof(CanApplyInviteHelperIdentityAction));
        OnPropertyChanged(nameof(CanClearInviteHelperIdentityAction));
        ApplyInviteHelperIdentityCommand.NotifyCanExecuteChanged();
        ClearInviteHelperIdentityCommand.NotifyCanExecuteChanged();
        if (refreshInvite)
        {
            EnsureInviteSnapshot(forceNewToken: true);
        }
    }

    private void ApplyInviteHelperIdentity()
    {
        if (!TryResolveInviteHelperIdentityInput(out var helperIdentity, out var normalizedInput))
        {
            return;
        }

        SetVerifiedInviteHelperIdentity(helperIdentity, refreshInvite: true, normalizedInputOverride: normalizedInput);
    }

    private void AutoApplyInviteHelperIdentityIfPossible()
    {
        if (!CanStartOrConnect)
        {
            return;
        }

        if (!TryResolveInviteHelperIdentityInput(out var helperIdentity, out var normalizedInput))
        {
            return;
        }

        if (string.Equals(verifiedInviteHelperIdentity, helperIdentity.Value, StringComparison.Ordinal))
        {
            return;
        }

        SetVerifiedInviteHelperIdentity(helperIdentity, refreshInvite: true, normalizedInputOverride: normalizedInput);
    }

    private bool CanApplyInviteHelperIdentity()
    {
        return CanStartOrConnect &&
               TryResolveInviteHelperIdentityInput(out var helperIdentity, out _) &&
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

    private bool TryResolveInviteHelperIdentityInput(out PeerAddress helperIdentity, out string normalizedInput)
    {
        normalizedInput = InviteHelperIdentityInput.Trim();
        if (normalizedInput.StartsWith(HelperIdentityTokenCodec.TokenPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var decodeResult = HelperIdentityTokenCodec.Decode(normalizedInput);
            if (decodeResult.IsSuccess && decodeResult.Address is not null)
            {
                helperIdentity = decodeResult.Address.Value;
                normalizedInput = HelperIdentityTokenCodec.Encode(helperIdentity);
                return true;
            }

            helperIdentity = default;
            return false;
        }

        if (PeerAddress.TryParse(normalizedInput, out helperIdentity))
        {
            normalizedInput = helperIdentity.Value;
            return true;
        }

        helperIdentity = default;
        return false;
    }

    private PeerAddress? ResolveVerifiedInviteHelperAddress()
    {
        return PeerAddress.TryParse(verifiedInviteHelperIdentity, out var parsed)
            ? parsed
            : null;
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
        value ??= string.Empty;
        if (SetProperty(ref shareInviteStatusText, value, nameof(ShareInviteStatusText)))
        {
            OnPropertyChanged(nameof(ShowShareInviteStatus));
        }
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
        wasConnected = true;
        endReason = null;
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
                if (!RestartWaitingSessionAfterTerminalSession())
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

    private void OnScreenShareStopped(object? sender, EventArgs e)
    {
        if (disposed)
        {
            return;
        }

        ApplyImmediateScreenSharePreviewStopState();
        RequestStopScreenSharePreview();
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
            OnPropertyChanged(nameof(ShowRemoteControlConsentDialog));
            StopControlCommand.NotifyCanExecuteChanged();
            RestartAsAdministratorCommand.NotifyCanExecuteChanged();
            AllowControlConsentCommand.NotifyCanExecuteChanged();
            DenyControlConsentCommand.NotifyCanExecuteChanged();
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

    private void OnSessionRuntimeStateChanged(object? sender, SessionRuntimeStateChangedEventArgs e)
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
        EnsureInviteSnapshot(forceNewToken: false);
        SyncIncomingApprovalRequestFromRuntime();

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
                            ResetIncomingApprovalRequestState();
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
        ToggleScreenSharePreviewCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SessionSupportsRemoteControl));
        OnPropertyChanged(nameof(ShowStopControlAction));
        OnPropertyChanged(nameof(CanStopControl));
        OnPropertyChanged(nameof(ShowRemoteControlActiveStatus));
        OnPropertyChanged(nameof(ShowRemoteControlAdminWarning));
        OnPropertyChanged(nameof(RemoteControlAdminWarningText));
        OnPropertyChanged(nameof(CanRestartAsAdministrator));
        OnPropertyChanged(nameof(ShowRemoteControlPreviewActiveCue));
        NotifyRemoteControlDiagnosticsChanged();
        OnPropertyChanged(nameof(ShowRemoteControlConsentDialog));
        StopControlCommand.NotifyCanExecuteChanged();
        RestartAsAdministratorCommand.NotifyCanExecuteChanged();
        AllowControlConsentCommand.NotifyCanExecuteChanged();
        DenyControlConsentCommand.NotifyCanExecuteChanged();
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
        UpdateUiFromSnapshot("refresh");
    }

    private void UpdateUiFromSnapshot(string source)
    {
        bool nextChatEnabled;
        bool nextCanStartOrConnect;
        bool nextCanEndSession;
        bool nextCanOpenDiagnostics;
        bool nextCanSendFiles;
        var fileTransferSnapshot = sessionRuntime.FileTransferSnapshot;
        InboundFileTransfer = FileTransferPanelItemViewModel.FromSnapshot(fileTransferSnapshot.Inbound);
        OutboundFileTransfer = FileTransferPanelItemViewModel.FromSnapshot(fileTransferSnapshot.Outbound);
        var hasActiveOutboundTransfer = fileTransferSnapshot.Outbound is { IsTerminal: false };
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
                    nextCanStartOrConnect = false;
                    nextCanOpenDiagnostics = openDiagnosticsAction is not null;
                    nextCanSendFiles = sessionRuntime.CanPerform(SessionCapability.FileTransfer) &&
                                       !hasActiveOutboundTransfer;
                    break;

                case SessionUiPhase.Connecting:
                    nextChatEnabled = false;
                    nextCanStartOrConnect = false;
                    nextCanOpenDiagnostics = openDiagnosticsAction is not null;
                    nextCanSendFiles = false;
                    break;

                case SessionUiPhase.Failed:
                case SessionUiPhase.Ended:
                    nextChatEnabled = false;
                    nextCanStartOrConnect = !IsStartupBlocked;
                    nextCanOpenDiagnostics = openDiagnosticsAction is not null;
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
                    nextCanOpenDiagnostics = openDiagnosticsAction is not null;
                    nextCanSendFiles = false;
                    break;
            }
        }

        IsChatInputEnabled = nextChatEnabled;
        CanStartOrConnect = nextCanStartOrConnect;
        CanEndSession = nextCanEndSession;
        CanOpenDiagnostics = nextCanOpenDiagnostics;
        CanSendFiles = nextCanSendFiles;

        OnPropertyChanged(nameof(ShowOpenDiagnosticsLink));
        OnPropertyChanged(nameof(ShowSendFileAction));
        OnPropertyChanged(nameof(CanSendFileAction));
        OpenDiagnosticsCommand.NotifyCanExecuteChanged();
        SendFileCommand.NotifyCanExecuteChanged();
        SendChatCommand.NotifyCanExecuteChanged();
        AcceptIncomingFileCommand.NotifyCanExecuteChanged();
        DeclineIncomingFileCommand.NotifyCanExecuteChanged();
        CancelFileTransferCommand.NotifyCanExecuteChanged();
        EndSessionCommand.NotifyCanExecuteChanged();
        LogCurrentChatPanelState(source);
        AssertUiConsistency();
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
        if (SetProperty(ref incomingHelperIdentity, helperIdentity ?? string.Empty, nameof(IncomingHelperIdentityText)))
        {
            OnPropertyChanged(nameof(IncomingHelperName));
            OnPropertyChanged(nameof(IncomingHelperVerificationCode));
            OnPropertyChanged(nameof(HasIncomingHelperVerificationCode));
            OnPropertyChanged(nameof(HeaderVerificationCodeText));
            OnPropertyChanged(nameof(ShowHeaderVerificationCode));
            OnPropertyChanged(nameof(FirstPillVerificationCodeText));
            OnPropertyChanged(nameof(ShowFirstPillVerificationCode));
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
                pendingAutoRegenerateAfterDisconnect = true;
                if (!RestartWaitingSessionAfterTerminalSession())
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
        incomingRequestExpiresAtUtc = DateTimeOffset.MinValue;
        UpdateIncomingRequestTimeoutText(string.Empty);
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
            RestartWaitingSession();
            return true;
        }
        finally
        {
            autoRegeneratingAfterDisconnect = false;
        }
    }

    private bool RestartWaitingSessionAfterTerminalSession()
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
        ChatDraft = string.Empty;
        ChatMessages.Clear();
        OnPropertyChanged(nameof(HasChatMessages));
        OnPropertyChanged(nameof(ShowNoMessagesPlaceholder));
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

        _ = StopScreenSharePreviewAsync();
    }

    private async Task StopScreenSharePreviewAsync()
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
    }

    private void ApplyImmediateScreenSharePreviewStopState()
    {
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
    }

    private async Task SyncTransportScreenShareWithPreviewAsync(bool isPreviewActive)
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
        catch
        {
            // Best-effort: the local preview remains the user-visible source of truth.
        }
    }

    private bool ShouldDeferTransportPreviewTeardownToSessionDisconnect()
    {
        return endSessionRequested || Volatile.Read(ref windowCloseDisconnectStarted) != 0;
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
