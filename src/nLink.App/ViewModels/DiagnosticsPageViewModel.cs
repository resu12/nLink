using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using NLink.App.Configuration;
using NLink.App.Services;
using NLink.App.Services.ScreenCapture;
using NLink.App.Threading;
using NLink.Core;
using NLink.Core.Chat;
using NLink.Core.Configuration;
using NLink.Core.Diagnostics;
using NLink.Core.Logging;
using NLink.Core.Metrics;
using NLink.Core.SessionConnect;
using NLink.Infra.Nkn;

namespace NLink.App.ViewModels;

public sealed class DiagnosticsPageViewModel : ViewModelBase, IDisposable
{
    private const string ScreenShareMaxFpsVariable = ScreenShareQualitySettings.ScreenShareMaxFpsVariable;
    private const string ScreenShareTransportMaxFpsVariable = ScreenShareQualitySettings.ScreenShareTransportMaxFpsVariable;
    private const string ScreenShareTransportAutotuneVariable = "NLINK_FEATURE_SCREENCAP_TRANSPORT_AUTOTUNE";
    private const string ScreenShareScaleVariable = ScreenShareQualitySettings.ScreenShareScaleVariable;
    private const string ScreenShareQualityProfileVariable = ScreenShareQualitySettings.ScreenShareQualityProfileVariable;

    private readonly InlineTransientText copyFeedback = new();
    private readonly string? bugReportUrl;
    private readonly DiagnosticsSnapshot runtimeDiagnosticsSnapshot;
    private readonly MetricsRegistry? metricsRegistry;
    private readonly ResourceRuntimeTracker? resourceRuntimeTracker;
    private readonly HangReportService? hangReportService;
    private readonly ITunaWalletLinkStore? tunaWalletLinkStore;
    private readonly ITunaWalletVerifier? tunaWalletVerifier;
    private readonly ITunaRuntimePilotService? tunaRuntimePilotService;
    private readonly Func<DateTimeOffset> nowProvider;
    private readonly Func<string> diagnosticsExportRootProvider;
    private readonly Action<string, string, string, string, string> persistScreenSharePresetInBackground;
    private readonly InviteSecurityStatus inviteSecurityStatus;
    private readonly NknRuntimeDiagnosticsSnapshot nknDiagnosticsSnapshot;
    private readonly PersistenceDiagnosticsSnapshot persistenceDiagnosticsSnapshot;
    private readonly ScreenShareEvidenceSnapshot screenShareEvidenceSnapshot;
    private readonly ScreenShareLiveDiagnosticsSnapshot screenShareLiveSnapshot;
    private TunaWalletLinkState tunaWalletState = TunaWalletLinkState.Unlinked;
    private bool isTunaWalletValidating;

    public DiagnosticsPageViewModel(
        Action backAction,
        TransportRuntimeConfig transportConfig,
        ShareMessageConfig? linksConfig = null,
        SessionRuntime? sessionRuntime = null,
        MetricsRegistry? metricsRegistry = null,
        ResourceRuntimeTracker? resourceRuntimeTracker = null,
        HangReportService? hangReportService = null,
        Func<DateTimeOffset>? nowProvider = null,
        Func<string>? diagnosticsExportRootProvider = null,
        Action<string, string, string, string, string>? screenSharePresetPersistence = null)
        : this(
            ScreenShareEvidenceLocator.CreateDefault(),
            backAction,
            transportConfig,
            linksConfig,
            sessionRuntime,
            metricsRegistry,
            resourceRuntimeTracker,
            hangReportService,
            nowProvider,
            diagnosticsExportRootProvider,
            screenSharePresetPersistence)
    {
    }

    internal DiagnosticsPageViewModel(
        ScreenShareEvidenceLocator screenShareEvidenceLocator,
        Action backAction,
        TransportRuntimeConfig transportConfig,
        ShareMessageConfig? linksConfig = null,
        SessionRuntime? sessionRuntime = null,
        MetricsRegistry? metricsRegistry = null,
        ResourceRuntimeTracker? resourceRuntimeTracker = null,
        HangReportService? hangReportService = null,
        Func<DateTimeOffset>? nowProvider = null,
        Func<string>? diagnosticsExportRootProvider = null,
        Action<string, string, string, string, string>? screenSharePresetPersistence = null,
        ITunaWalletLinkStore? tunaWalletLinkStore = null,
        ITunaWalletVerifier? tunaWalletVerifier = null,
        ITunaRuntimePilotService? tunaRuntimePilotService = null)
    {
        linksConfig ??= new ShareMessageConfig(null);
        BackCommand = new RelayCommand(backAction);
        bugReportUrl = linksConfig.BugReportUrl;
        this.metricsRegistry = metricsRegistry;
        this.resourceRuntimeTracker = resourceRuntimeTracker;
        this.hangReportService = hangReportService;
        this.tunaWalletLinkStore = tunaWalletLinkStore;
        this.tunaWalletVerifier = tunaWalletVerifier;
        this.tunaRuntimePilotService = tunaRuntimePilotService;
        if (this.tunaRuntimePilotService is not null)
        {
            this.tunaRuntimePilotService.StateChanged += OnTunaRuntimeStateChanged;
        }

        this.nowProvider = nowProvider ?? DefaultNowProvider;
        this.diagnosticsExportRootProvider = diagnosticsExportRootProvider ?? DefaultDiagnosticsExportRootProvider;
        persistScreenSharePresetInBackground = screenSharePresetPersistence ?? PersistScreenSharePresetInBackground;
        tunaWalletState = LoadTunaWalletState(tunaWalletLinkStore);
        screenShareEvidenceSnapshot = screenShareEvidenceLocator.ReadLatest();
        screenShareLiveSnapshot = sessionRuntime?.GetScreenShareLiveDiagnosticsSnapshot() ?? ScreenShareLiveDiagnosticsSnapshot.Unavailable;

        ActiveTransport = transportConfig.DisplayName;
        TransportKey = transportConfig.Key;
        TransportSummary = transportConfig.Key;
        BuildMode = transportConfig.BuildMode;
        EnvironmentValue = transportConfig.EnvironmentVariableValue;
        SelectionReason = transportConfig.SelectionReason;
        AutoSelected = transportConfig.AutoSelected ? "Yes" : "No";
        ForcedByEnvironment = transportConfig.ForcedByEnvironment ? "Yes" : "No";
        inviteSecurityStatus = InviteSecurityDiagnostics.Snapshot();
        EmbeddedWebViewDefault = AppFeatureFlags.UseEmbeddedWebView ? "Enabled by default" : "Disabled by default";
        ScreenShareScaffold = FormatFeatureFlag(FeatureFlags.EnableScreenShareScaffold);
        SessionHeader = FormatFeatureFlag(FeatureFlags.EnableSessionHeader);
        AppVersion = ResolveAppVersion();
        OsDescription = RuntimeInformation.OSDescription;
        ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString();
        OsArchitecture = RuntimeInformation.OSArchitecture.ToString();
        BridgeResolutionRid = ResolveBridgeRidForDiagnostics();
        persistenceDiagnosticsSnapshot = PersistenceDiagnostics.Snapshot();
        runtimeDiagnosticsSnapshot = sessionRuntime?.GetDiagnosticsSnapshot() ?? new DiagnosticsSnapshot(
            CurrentState: "(unknown)",
            SessionUiState: "(unknown)",
            AttemptNumber: 0,
            LastFailureCategory: "(none)",
            LastFailureMessage: "(none)",
            LastConnectDurationMs: null,
            LastHandshakeDurationMs: null,
            LastBridgeStartDurationMs: null,
            PersistenceSummary: persistenceDiagnosticsSnapshot.Summary,
            PersistenceWarning: persistenceDiagnosticsSnapshot.LastWarning);

        if (string.Equals(transportConfig.Key, "NKN", StringComparison.OrdinalIgnoreCase))
        {
            NknRuntimeDiagnostics.EnsureInitialized();
        }

        var counters = ChatRuntimeCounters.Snapshot();
        nknDiagnosticsSnapshot = NknRuntimeDiagnostics.Snapshot();
        NknAddress = nknDiagnosticsSnapshot.Address;
        MessagesSent = nknDiagnosticsSnapshot.MessagesSent.ToString();
        MessagesReceived = nknDiagnosticsSnapshot.MessagesReceived.ToString();
        LastError = nknDiagnosticsSnapshot.LastError;
        BridgePid = nknDiagnosticsSnapshot.BridgePid > 0 ? nknDiagnosticsSnapshot.BridgePid.ToString() : "(not running)";
        NodeSdk = string.IsNullOrWhiteSpace(nknDiagnosticsSnapshot.NodeVersion) ? "(unknown)" : nknDiagnosticsSnapshot.NodeVersion;
        LastHeartbeat = nknDiagnosticsSnapshot.BridgeLastPongUtcTicks > 0
            ? new DateTimeOffset(nknDiagnosticsSnapshot.BridgeLastPongUtcTicks, TimeSpan.Zero).ToString("u")
            : "(none)";
        BridgeRestarts = nknDiagnosticsSnapshot.BridgeRestartCount.ToString();
        LastBridgeExit = BuildLastBridgeExitText(nknDiagnosticsSnapshot.BridgeLastExitCode, nknDiagnosticsSnapshot.BridgeLastExitReason);
        BridgeRawMessagesReceived = nknDiagnosticsSnapshot.BridgeRawMessagesReceived.ToString();
        ScreenShareOutboundBusyDrops = nknDiagnosticsSnapshot.ScreenShareOutboundBusyDrops.ToString();
        ScreenSharePayloadBytesSent = nknDiagnosticsSnapshot.ScreenSharePayloadBytesSent.ToString();
        ScreenShareMessagesSent = nknDiagnosticsSnapshot.ScreenShareMessagesSent.ToString();
        ScreenShareBridgeBytesSent = nknDiagnosticsSnapshot.ScreenShareBridgeBytesSent.ToString();
        HighPriorityControlQueueOverflows = nknDiagnosticsSnapshot.HighPriorityControlQueueOverflows.ToString();
        HighPriorityControlRejected = nknDiagnosticsSnapshot.HighPriorityControlRejected.ToString();
        HighPriorityControlCoalesced = nknDiagnosticsSnapshot.HighPriorityControlCoalesced.ToString();
        HighPriorityControlDroppedForStop = nknDiagnosticsSnapshot.HighPriorityControlDroppedForStop.ToString();
        LastBridgeMessageSource = nknDiagnosticsSnapshot.LastBridgeMessageSource;
        LastBridgeMessageKind = BuildBridgeMessageKind(nknDiagnosticsSnapshot.LastBridgeMessageIsTopic);
        LastEnvelopeType = nknDiagnosticsSnapshot.LastEnvelopeType;
        LastEnvelopeDropReason = nknDiagnosticsSnapshot.LastEnvelopeDropReason;
        JoinRequestsReceived = nknDiagnosticsSnapshot.JoinRequestsReceived.ToString();
        IncomingJoinRequestRaisedCount = nknDiagnosticsSnapshot.IncomingJoinRequestRaisedCount.ToString();
        AcksReceived = nknDiagnosticsSnapshot.AcksReceived.ToString();
        AcksIgnoredSourceMismatch = nknDiagnosticsSnapshot.AcksIgnoredSourceMismatch.ToString();
        LastDisconnectReason = nknDiagnosticsSnapshot.LastDisconnectReason;
        HelperAddressSource = nknDiagnosticsSnapshot.HelperAddressSource;
        HelperAddressAuthoritative = nknDiagnosticsSnapshot.HelperAddressAuthoritative ? "Yes" : "No";
        HelperVerificationCodeVisible = nknDiagnosticsSnapshot.HelperVerificationCodeVisible ? "Yes" : "No";
        HelperIdentityRegeneratedCount = nknDiagnosticsSnapshot.HelperIdentityRegeneratedCount.ToString(CultureInfo.InvariantCulture);
        HelperIdentityLastRegeneratedUtc = nknDiagnosticsSnapshot.HelperIdentityLastRegeneratedUtcTicks > 0
            ? new DateTimeOffset(nknDiagnosticsSnapshot.HelperIdentityLastRegeneratedUtcTicks, TimeSpan.Zero).ToString("u")
            : "(none)";
        FirstColdStartObserved = nknDiagnosticsSnapshot.FirstColdStartObserved ? "Yes" : "No";
        FirstColdStartMs = nknDiagnosticsSnapshot.FirstColdStartObserved && nknDiagnosticsSnapshot.FirstColdStartMs >= 0
            ? nknDiagnosticsSnapshot.FirstColdStartMs.ToString("F2")
            : "(none)";
        FirstColdStartRecordedUtc = nknDiagnosticsSnapshot.FirstColdStartUtcTicks > 0
            ? new DateTimeOffset(nknDiagnosticsSnapshot.FirstColdStartUtcTicks, TimeSpan.Zero).ToString("u")
            : "(none)";
        ChatSent = counters.ChatSent.ToString();
        ChatReceived = counters.ChatReceived.ToString();
        DecryptFailed = counters.ChatDecryptFailed.ToString();
        RecentConnectionAttemptsText = BuildRecentConnectionAttemptsText(SessionReliabilityLog.SnapshotRecent(10));
        CopyReliabilityLogCommand = new RelayCommand(RequestCopyReliabilityLog);
        SaveHangReportCommand = new RelayCommand(SaveHangReport);
        ExportMetricsJsonCommand = new RelayCommand(ExportMetricsJson);
        OpenLogsFolderCommand = new RelayCommand(RequestOpenLogsFolder);
        ReportBugCommand = new RelayCommand(RequestOpenBugReport);
        ApplyBalancedScreenSharePresetCommand = new RelayCommand(ApplyBalancedScreenSharePreset);
        ApplyHighQualityScreenSharePresetCommand = new RelayCommand(ApplyHighQualityScreenSharePreset);
        ApplyTunaQualityScreenSharePresetCommand = new RelayCommand(ApplyTunaQualityScreenSharePreset);
        ApplyHighPerformanceScreenSharePresetCommand = new RelayCommand(ApplyHighPerformanceScreenSharePreset);
        LinkTunaWalletCommand = new RelayCommand(RequestLinkTunaWallet);
        ValidateTunaWalletCommand = new RelayCommand(RequestValidateTunaWallet, CanValidateTunaWallet);
        CopyTunaWalletAddressCommand = new RelayCommand(RequestCopyTunaWalletAddress, CanCopyTunaWalletAddress);
        UnlinkTunaWalletCommand = new RelayCommand(UnlinkTunaWallet, CanUnlinkTunaWallet);
        UnlockTunaRuntimeCommand = new RelayCommand(RequestUnlockTunaRuntime, CanUnlockTunaRuntime);
    }

    public string PageTitle => "Options";

    public string PageSubtitle => "Settings, wallet, and support diagnostics.";

    public string ActiveTransport { get; }

    public string TransportKey { get; }

    public string TransportSummary { get; }

    public string BuildMode { get; }

    public string EnvironmentValue { get; }

    public string SelectionReason { get; }

    public string AutoSelected { get; }

    public string ForcedByEnvironment { get; }

    public string EmbeddedWebViewDefault { get; }

    public string InviteSecurityMode => inviteSecurityStatus.Mode;

    public string InviteSigningConfiguration => inviteSecurityStatus.SigningConfiguration;

    public string InvitePublicFlow => inviteSecurityStatus.PublicInviteFlow;

    public string InviteSecurityReleaseReady => inviteSecurityStatus.ReleaseReady ? "Yes" : "No";

    public string InviteSecurityWarning => inviteSecurityStatus.Warning;

    public string ScreenShareScaffold { get; }

    public string SessionHeader { get; }
    public int ScreenShareCaptureMaxFps => FeatureFlags.ScreenShareMaxFps;
    public int ScreenShareTransportMaxFps => FeatureFlags.ScreenShareTransportMaxFps;
    public string ScreenShareCaptureScale => ScreenShareQualitySettings.FormatScale(FeatureFlags.ScreenShareScale);
    public string ScreenShareQualityProfile => FeatureFlags.ScreenShareQualityProfile;
    public string ScreenShareEffectivePresetName => ScreenShareQualitySettings.GetCurrentEnvironmentState().EffectivePresetName;
    public string ScreenSharePresetMigrationStatus => ScreenShareQualitySettings.WasLegacyHigherClarityPresetMigrated ? "Yes" : "No";
    public string ScreenSharePresetBalanced => $"Good default for most sessions. {ScreenShareQualitySettings.BalancedPreset.DescribeForOptions()}.";
    public string ScreenSharePresetHighQuality => $"Smoother motion over regular NKN. {ScreenShareQualitySettings.HighQualityPreset.DescribeForOptions()}.";
    public string ScreenSharePresetTunaQuality => $"Highest quality; recommended with Tuna. {ScreenShareQualitySettings.TunaQualityPreset.DescribeForOptions()}.";
    public string ScreenSharePresetHighPerformance => $"Lower bandwidth for slower connections. {ScreenShareQualitySettings.HighPerformancePreset.DescribeForOptions()}.";
    public string ScreenShareCaptureEnvHint => "Apply a preset, then restart screen sharing if it is already running. nLink may still reduce quality automatically if the connection is congested.";
    public string ScreenShareEvidenceStatus => screenShareEvidenceSnapshot.StatusKey;
    public string ScreenShareEvidenceArtifactName => screenShareEvidenceSnapshot.ArtifactName;
    public string ScreenShareEvidenceVerdict => screenShareEvidenceSnapshot.OperatorVerdict;
    public string ScreenShareEvidenceSummary => screenShareEvidenceSnapshot.OperatorSummary;
    public string ScreenShareEvidenceNextAction => screenShareEvidenceSnapshot.NextOperatorAction;
    public string ScreenShareEvidenceDeepestClassification => screenShareEvidenceSnapshot.DeepestTrackBClassification;
    public string ScreenShareEvidenceQualitySummary => screenShareEvidenceSnapshot.QualityProfileSummary;
    public string ScreenShareEvidencePerformanceSummary => screenShareEvidenceSnapshot.PerformanceSummary;
    public string ScreenShareEvidenceCursorSummary => screenShareEvidenceSnapshot.CursorSummary;
    public string ScreenShareEvidenceVisualSafetySummary => screenShareEvidenceSnapshot.VisualSafetySummary;
    public string ScreenShareEvidenceLowFpsSummary => screenShareEvidenceSnapshot.LowFpsSummary;
    public string ScreenShareEvidenceExternalTopologySummary => screenShareEvidenceSnapshot.ExternalTopologySummary;

    public string AppVersion { get; }

    public string OsDescription { get; }

    public string ProcessArchitecture { get; }

    public string OsArchitecture { get; }

    public string BridgeResolutionRid { get; }
    public string CurrentTransportState => runtimeDiagnosticsSnapshot.CurrentState;
    public string SessionUiState => runtimeDiagnosticsSnapshot.SessionUiState;
    public string LastFailureCategory => runtimeDiagnosticsSnapshot.LastFailureCategory;
    public string LastFailureMessage => runtimeDiagnosticsSnapshot.LastFailureMessage;
    public string AttemptNumber => runtimeDiagnosticsSnapshot.AttemptNumber.ToString();
    public string LastConnectDurationMs => FormatDuration(runtimeDiagnosticsSnapshot.LastConnectDurationMs);
    public string LastHandshakeDurationMs => FormatDuration(runtimeDiagnosticsSnapshot.LastHandshakeDurationMs);
    public string LastBridgeStartDurationMs => FormatDuration(runtimeDiagnosticsSnapshot.LastBridgeStartDurationMs);
    public string RuntimeSummary => runtimeDiagnosticsSnapshot.RuntimeSummary;
    public string AuthorizationSummary => runtimeDiagnosticsSnapshot.AuthorizationSummary;
    public string LastAuthorizationDenialReason => runtimeDiagnosticsSnapshot.LastAuthorizationDenialReason;
    public string SessionSecuritySummary => runtimeDiagnosticsSnapshot.SessionSecuritySummary;
    public string RemoteControlSummary => runtimeDiagnosticsSnapshot.RemoteControlSummary;
    public string ScreenShareSummary => runtimeDiagnosticsSnapshot.ScreenShareSummary;
    public string FileTransferSummary => runtimeDiagnosticsSnapshot.FileTransferSummary;
    public string PersistenceSummary => runtimeDiagnosticsSnapshot.PersistenceSummary;
    public string PersistenceWarning => runtimeDiagnosticsSnapshot.PersistenceWarning;
    public string DiagnosticsPrivacyNotice => DiagnosticsExportBuilder.BestEffortPrivacyNotice;
    public string TunaRuntimeFlagStatus
        => NknTunaAccelerationOptions.Load().Enabled
            ? "Enabled by runtime flag"
            : CurrentTunaRuntimePreferences().Enabled
                ? "Advanced opt-in"
                : "Off";
    public string TunaFallbackState => NknTunaAccelerationOptions.Load().Enabled
        ? "Experimental acceleration can negotiate only after an approved secure session."
        : CurrentTunaRuntimePreferences().Enabled
            ? "Tuna can start only after approved session unlock; current NKN remains fallback."
            : "Current NKN will be used.";
    public bool IsTunaRuntimeEnabled
    {
        get => CurrentTunaRuntimePreferences().Enabled;
        set
        {
            var current = CurrentTunaRuntimePreferences();
            if (current.Enabled == value)
            {
                return;
            }

            if (!value)
            {
                _ = tunaRuntimePilotService?.LockOrStopForSessionAsync(
                    "runtime_disabled",
                    TunaRuntimeUnlockSource.Options,
                    CancellationToken.None);
            }

            SaveTunaRuntimePreferences(new TunaRuntimePreferenceState
            {
                Enabled = value,
                FileLaneEnabled = current.FileLaneEnabled,
                ScreenLaneEnabled = current.ScreenLaneEnabled,
                MaxPriceNknPerMb = current.MaxPriceNknPerMb,
                MaxTotalMiB = current.MaxTotalMiB,
                MaxDurationSec = current.MaxDurationSec,
                AllowDegradedProviderReady = current.AllowDegradedProviderReady,
                LastRuntimeStatus = value ? "locked" : "off",
            });
        }
    }

    public bool IsTunaFileLaneEnabled
    {
        get => CurrentTunaRuntimePreferences().FileLaneEnabled;
        set
        {
            var current = CurrentTunaRuntimePreferences();
            if (current.FileLaneEnabled == value)
            {
                return;
            }

            if (!value && !current.ScreenLaneEnabled)
            {
                copyFeedback.Show("Choose at least one Tuna lane");
                OnPropertyChanged(nameof(IsTunaFileLaneEnabled));
                return;
            }

            SaveTunaRuntimePreferences(new TunaRuntimePreferenceState
            {
                Enabled = current.Enabled,
                FileLaneEnabled = value,
                ScreenLaneEnabled = current.ScreenLaneEnabled,
                MaxPriceNknPerMb = current.MaxPriceNknPerMb,
                MaxTotalMiB = current.MaxTotalMiB,
                MaxDurationSec = current.MaxDurationSec,
                AllowDegradedProviderReady = current.AllowDegradedProviderReady,
                LastRuntimeStatus = current.LastRuntimeStatus,
            });
        }
    }

    public bool IsTunaScreenLaneEnabled
    {
        get => CurrentTunaRuntimePreferences().ScreenLaneEnabled;
        set
        {
            var current = CurrentTunaRuntimePreferences();
            if (current.ScreenLaneEnabled == value)
            {
                return;
            }

            if (!value && !current.FileLaneEnabled)
            {
                copyFeedback.Show("Choose at least one Tuna lane");
                OnPropertyChanged(nameof(IsTunaScreenLaneEnabled));
                return;
            }

            SaveTunaRuntimePreferences(new TunaRuntimePreferenceState
            {
                Enabled = current.Enabled,
                FileLaneEnabled = current.FileLaneEnabled,
                ScreenLaneEnabled = value,
                MaxPriceNknPerMb = current.MaxPriceNknPerMb,
                MaxTotalMiB = current.MaxTotalMiB,
                MaxDurationSec = current.MaxDurationSec,
                AllowDegradedProviderReady = current.AllowDegradedProviderReady,
                LastRuntimeStatus = current.LastRuntimeStatus,
            });
        }
    }

    public string TunaMaxPriceNknPerMb
    {
        get => CurrentTunaRuntimePreferences().MaxPriceNknPerMb;
        set
        {
            var current = CurrentTunaRuntimePreferences();
            SaveTunaRuntimePreferences(new TunaRuntimePreferenceState
            {
                Enabled = current.Enabled,
                FileLaneEnabled = current.FileLaneEnabled,
                ScreenLaneEnabled = current.ScreenLaneEnabled,
                MaxPriceNknPerMb = value,
                MaxTotalMiB = current.MaxTotalMiB,
                MaxDurationSec = current.MaxDurationSec,
                AllowDegradedProviderReady = current.AllowDegradedProviderReady,
                LastRuntimeStatus = current.LastRuntimeStatus,
            });
        }
    }

    public string TunaMaxTotalMiB
    {
        get => CurrentTunaRuntimePreferences().MaxTotalMiB.ToString(CultureInfo.InvariantCulture);
        set
        {
            var current = CurrentTunaRuntimePreferences();
            SaveTunaRuntimePreferences(new TunaRuntimePreferenceState
            {
                Enabled = current.Enabled,
                FileLaneEnabled = current.FileLaneEnabled,
                ScreenLaneEnabled = current.ScreenLaneEnabled,
                MaxPriceNknPerMb = current.MaxPriceNknPerMb,
                MaxTotalMiB = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : current.MaxTotalMiB,
                MaxDurationSec = current.MaxDurationSec,
                AllowDegradedProviderReady = current.AllowDegradedProviderReady,
                LastRuntimeStatus = current.LastRuntimeStatus,
            });
        }
    }

    public string TunaMaxDurationMinutes
    {
        get => Math.Max(1, CurrentTunaRuntimePreferences().MaxDurationSec / 60).ToString(CultureInfo.InvariantCulture);
        set
        {
            var current = CurrentTunaRuntimePreferences();
            SaveTunaRuntimePreferences(new TunaRuntimePreferenceState
            {
                Enabled = current.Enabled,
                FileLaneEnabled = current.FileLaneEnabled,
                ScreenLaneEnabled = current.ScreenLaneEnabled,
                MaxPriceNknPerMb = current.MaxPriceNknPerMb,
                MaxTotalMiB = current.MaxTotalMiB,
                MaxDurationSec = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed * 60 : current.MaxDurationSec,
                AllowDegradedProviderReady = current.AllowDegradedProviderReady,
                LastRuntimeStatus = current.LastRuntimeStatus,
            });
        }
    }

    public string TunaRuntimeStatus => tunaRuntimePilotService?.RuntimeStatus ?? "service unavailable";
    public string TunaCurrentState => TunaStatusPresentationMapper.FromRuntimeStatus(TunaRuntimeStatus).Text;
    public string TunaStartupTiming => tunaRuntimePilotService?.StartupTimingSummary ?? "(none)";
    public string TunaRuntimeUnlockStatus => GetTunaRuntimeUnlockState().StatusText;
    public string TunaRuntimePayerNotice => "This computer pays while acting as the Tuna listener.";
    public string TunaSpendByNLink => FormatTunaSpend(CurrentTunaUsage());
    public string TunaAverageCost => FormatTunaAverageCost(CurrentTunaUsage());
    public string TunaLastSessionCost => FormatTunaLastSessionCost(CurrentTunaUsage());
    public string TunaLastSessionReason => FormatTunaLastSessionReason(CurrentTunaUsage());
    public string TunaSidecarVerifierStatus
    {
        get
        {
            var availability = tunaWalletVerifier?.GetAvailability();
            if (availability is null)
            {
                return "Unavailable";
            }

            if (availability.IsAvailable)
            {
                return "Available";
            }

            return availability.Status switch
            {
                "sidecar_missing" => "Missing",
                "sidecar_version_mismatch" => "Wrong version",
                "sidecar_protocol_mismatch" or
                "sidecar_app_protocol_mismatch" or
                "sidecar_frame_protocol_mismatch" => "Protocol mismatch",
                "sidecar_manifest_missing" or
                "sidecar_manifest_invalid" or
                "sidecar_manifest_hash_mismatch" => "Manifest invalid",
                _ => "Unavailable",
            };
        }
    }

    public string TunaSidecarVerifierDetail
    {
        get
        {
            var availability = tunaWalletVerifier?.GetAvailability();
            if (availability is null)
            {
                return "verifier service missing";
            }

            if (!string.IsNullOrWhiteSpace(availability.Detail))
            {
                return availability.Detail;
            }

            if (availability.IsAvailable && !string.IsNullOrWhiteSpace(availability.SidecarPath))
            {
                return Path.GetFileName(availability.SidecarPath);
            }

            return availability.Status;
        }
    }
    public string TunaWalletFileName => tunaWalletState.WalletFileName;
    public string TunaWalletStatus => FormatTunaWalletStatus(tunaWalletState.Status, isTunaWalletValidating);
    public string TunaWalletAddress => string.IsNullOrWhiteSpace(tunaWalletState.WalletAddress)
        ? "(not verified)"
        : tunaWalletState.WalletAddress;
    public string TunaWalletBalance => string.IsNullOrWhiteSpace(tunaWalletState.BalanceNkn)
        ? "(unknown)"
        : $"{tunaWalletState.BalanceNkn} NKN";
    public string TunaWalletBalanceCategory => tunaWalletState.BalanceCategory;
    public string TunaWalletLastVerified => tunaWalletState.LastVerifiedUtc.HasValue
        ? tunaWalletState.LastVerifiedUtc.Value.ToString("u")
        : "(never)";
    public string TunaWalletLastFailure => string.IsNullOrWhiteSpace(tunaWalletState.LastFailureReason)
        ? "(none)"
        : tunaWalletState.LastFailureReason;
    public bool IsTunaWalletLinked => tunaWalletState.IsLinked;
    public bool IsTunaWalletValidating => isTunaWalletValidating;
    public bool ShowTunaWalletFailure => tunaWalletState.Status == TunaWalletLinkStatus.ValidationFailed &&
        !string.IsNullOrWhiteSpace(tunaWalletState.LastFailureReason);
    public string AuthoritativeConnectedAddress => nknDiagnosticsSnapshot.AuthoritativeConnectedAddressResolved ? "Yes" : "No";
    public string LastRejectedMessageSummary => BuildLastRejectedMessageSummary();
    public string ConnectionStateSummary => $"session={SessionUiState}; transport={CurrentTransportState}; role_state={TransportSummary}";
    public string ConnectionErrorSummary => BuildConnectionErrorSummary();
    public string HelperIdentitySummary => BuildHelperIdentitySummary();
    public string ScreenShareLiveState => screenShareLiveSnapshot.AnyScreenShareActive ? "Active" : "(not sharing)";
    public string ScreenShareLiveProfileSummary => BuildScreenShareLiveProfileSummary();
    public string ScreenShareLivePerformanceSummary => BuildScreenShareLivePerformanceSummary();
    public string ScreenShareLiveCpuSummary => BuildScreenShareLiveCpuSummary();
    public string ScreenShareLiveCursorSummary => BuildScreenShareLiveCursorSummary();
    public string ScreenShareLiveVisualSafetySummary => BuildScreenShareLiveVisualSafetySummary();
    public string AdvancedScreenShareSettingsSummary => BuildAdvancedScreenShareSettingsSummary();
    public bool ShowScreenShareResetHint => ScreenShareQualitySettings.WasLegacyHigherClarityPresetMigrated ||
        string.Equals(ScreenShareEffectivePresetName, "Custom", StringComparison.OrdinalIgnoreCase);

    public string NknAddress { get; }

    public string MessagesSent { get; }

    public string MessagesReceived { get; }

    public string ChatSent { get; }

    public string ChatReceived { get; }

    public string DecryptFailed { get; }

    public string LastError { get; }

    public string BridgePid { get; }

    public string NodeSdk { get; }

    public string LastHeartbeat { get; }

    public string BridgeRestarts { get; }

    public string LastBridgeExit { get; }
    public string BridgeManifestSummary => BuildBridgeManifestSummary();

    public string BridgeRawMessagesReceived { get; }

    public string ScreenShareOutboundBusyDrops { get; }

    public string ScreenSharePayloadBytesSent { get; }

    public string ScreenShareMessagesSent { get; }

    public string ScreenShareBridgeBytesSent { get; }

    public string HighPriorityControlQueueOverflows { get; }

    public string HighPriorityControlRejected { get; }

    public string HighPriorityControlCoalesced { get; }

    public string HighPriorityControlDroppedForStop { get; }

    public string LastBridgeMessageSource { get; }

    public string LastBridgeMessageKind { get; }

    public string LastEnvelopeType { get; }

    public string LastEnvelopeDropReason { get; }

    public string JoinRequestsReceived { get; }

    public string IncomingJoinRequestRaisedCount { get; }

    public string AcksReceived { get; }

    public string AcksIgnoredSourceMismatch { get; }

    public string LastDisconnectReason { get; }
    public string HelperAddressSource { get; }
    public string HelperAddressAuthoritative { get; }
    public string HelperVerificationCodeVisible { get; }
    public string HelperIdentityRegeneratedCount { get; }
    public string HelperIdentityLastRegeneratedUtc { get; }
    public string FirstColdStartObserved { get; }
    public string FirstColdStartMs { get; }
    public string FirstColdStartRecordedUtc { get; }

    public string RecentConnectionAttemptsTitle => "Recent connection attempts";

    public string RecentConnectionAttemptsText { get; }

    public bool ShowCopyFeedback
        => copyFeedback.IsVisible;

    public string CopyFeedbackText => copyFeedback.Text;
    public InlineTransientText CopyFeedback => copyFeedback;

    public IRelayCommand CopyReliabilityLogCommand { get; }
    public IRelayCommand SaveHangReportCommand { get; }
    public IRelayCommand ExportMetricsJsonCommand { get; }

    public IRelayCommand OpenLogsFolderCommand { get; }

    public IRelayCommand ReportBugCommand { get; }
    public bool ShowReportBug => !string.IsNullOrWhiteSpace(bugReportUrl);
    public IRelayCommand ApplyBalancedScreenSharePresetCommand { get; }
    public IRelayCommand ApplyHighQualityScreenSharePresetCommand { get; }
    public IRelayCommand ApplyTunaQualityScreenSharePresetCommand { get; }
    public IRelayCommand ApplyHighPerformanceScreenSharePresetCommand { get; }
    public IRelayCommand LinkTunaWalletCommand { get; }
    public IRelayCommand ValidateTunaWalletCommand { get; }
    public IRelayCommand UnlockTunaRuntimeCommand { get; }
    public IRelayCommand CopyTunaWalletAddressCommand { get; }
    public IRelayCommand UnlinkTunaWalletCommand { get; }

    public IRelayCommand BackCommand { get; }

    public event EventHandler<string>? CopyReliabilityLogRequested;
    public event EventHandler? LinkTunaWalletRequested;
    public event EventHandler? ValidateTunaWalletPasswordRequested;
    public event EventHandler? UnlockTunaRuntimePasswordRequested;
    public event EventHandler<string>? CopyTunaWalletAddressRequested;

    public event EventHandler<string>? OpenLogsFolderRequested;

    public event EventHandler<string>? OpenBugReportRequested;
    public event EventHandler<string>? OpenMetricsExportFolderRequested;
    public event EventHandler<string>? OpenHangReportFolderRequested;

    public void NotifyCopySucceeded()
    {
        copyFeedback.Show("Copied");
    }

    public void NotifyCopyFailed()
    {
        copyFeedback.Show("Could not copy");
    }

    public void NotifyTunaWalletAddressCopied()
    {
        copyFeedback.Show("Wallet address copied");
    }

    public void NotifyTunaWalletAddressCopyFailed()
    {
        copyFeedback.Show("Could not copy wallet address");
    }

    public async Task LinkTunaWalletAsync(string? walletPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(walletPath))
        {
            return;
        }

        try
        {
            var state = TunaWalletLinkState.Linked(walletPath, nowProvider());
            await SaveTunaWalletStateAsync(state, ct);
            copyFeedback.Show("Wallet linked");
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn("Diagnostics", $"Tuna wallet link failed: {ex.GetType().Name}");
            copyFeedback.Show("Could not link wallet");
        }
    }

    public async Task ValidateTunaWalletAsync(char[]? password, CancellationToken ct = default)
    {
        if (!tunaWalletState.IsLinked || string.IsNullOrWhiteSpace(tunaWalletState.WalletPath))
        {
            copyFeedback.Show("Link a wallet first");
            return;
        }

        if (password is null || password.Length == 0)
        {
            copyFeedback.Show("Password required");
            return;
        }

        if (isTunaWalletValidating)
        {
            return;
        }

        SetTunaWalletValidating(true);
        try
        {
            var result = tunaWalletVerifier is null
                ? TunaWalletValidationResult.Fail("verifier_service_missing", tunaWalletState.WalletFileName)
                : await tunaWalletVerifier.ValidateAsync(tunaWalletState.WalletPath, password, ct);
            var nextState = tunaWalletState.WithValidationResult(result, nowProvider());
            await SaveTunaWalletStateAsync(nextState, ct);
            copyFeedback.Show(result.Success
                ? (nextState.Status == TunaWalletLinkStatus.VerifiedFunded ? "Wallet verified" : "Wallet is empty")
                : "Wallet validation failed");
        }
        finally
        {
            Array.Clear(password);
            SetTunaWalletValidating(false);
        }
    }

    public async Task UnlockTunaRuntimeAsync(char[]? password, CancellationToken ct = default)
    {
        if (tunaRuntimePilotService is null)
        {
            copyFeedback.Show("Tuna runtime unavailable");
            if (password is not null)
            {
                Array.Clear(password);
            }

            return;
        }

        if (password is null || password.Length == 0)
        {
            copyFeedback.Show("Password required");
            if (password is not null)
            {
                Array.Clear(password);
            }

            return;
        }

        SetTunaWalletValidating(true);
        try
        {
            var result = await tunaRuntimePilotService
                .UnlockForSessionAsync(password, TunaRuntimeUnlockSource.Options, ct)
                .ConfigureAwait(false);
            tunaWalletState = await LoadTunaWalletStateAsync(ct).ConfigureAwait(false);
            copyFeedback.Show(result.Message);
            RefreshTunaRuntimeProperties();
            RefreshTunaWalletProperties();
        }
        finally
        {
            SetTunaWalletValidating(false);
        }
    }

    private static string BuildLastBridgeExitText(int exitCode, string reason)
    {
        var safeReason = string.IsNullOrWhiteSpace(reason) ? "(none)" : reason;
        if (exitCode < 0)
        {
            return safeReason;
        }

        return $"Code {exitCode}: {safeReason}";
    }

    private static string FormatFeatureFlag(bool enabled) => enabled ? "On" : "Off";

    private static TunaWalletLinkState LoadTunaWalletState(ITunaWalletLinkStore? store)
    {
        if (store is null)
        {
            return TunaWalletLinkState.Unlinked;
        }

        try
        {
            return store.LoadAsync().GetAwaiter().GetResult();
        }
        catch
        {
            return TunaWalletLinkState.Unlinked;
        }
    }

    private async Task<TunaWalletLinkState> LoadTunaWalletStateAsync(CancellationToken ct)
    {
        if (tunaWalletLinkStore is null)
        {
            return TunaWalletLinkState.Unlinked;
        }

        try
        {
            return await tunaWalletLinkStore.LoadAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return TunaWalletLinkState.Unlinked;
        }
    }

    private async Task SaveTunaWalletStateAsync(TunaWalletLinkState state, CancellationToken ct)
    {
        if (tunaWalletLinkStore is not null)
        {
            await tunaWalletLinkStore.SaveAsync(state, ct);
        }

        tunaWalletState = state;
        RefreshTunaWalletProperties();
        RefreshTunaRuntimeProperties();
    }

    private void SetTunaWalletValidating(bool value)
    {
        if (!UiThreadDispatch.CheckAccess())
        {
            _ = UiThreadDispatch.RunAsync(() => SetTunaWalletValidating(value));
            return;
        }

        if (SetProperty(ref isTunaWalletValidating, value, nameof(IsTunaWalletValidating)))
        {
            RefreshTunaWalletProperties();
        }
    }

    private void RefreshTunaWalletProperties()
    {
        if (!UiThreadDispatch.CheckAccess())
        {
            _ = UiThreadDispatch.RunAsync(RefreshTunaWalletPropertiesCore);
            return;
        }

        RefreshTunaWalletPropertiesCore();
    }

    private void RefreshTunaWalletPropertiesCore()
    {
        OnPropertyChanged(nameof(TunaRuntimeFlagStatus));
        OnPropertyChanged(nameof(TunaFallbackState));
        OnPropertyChanged(nameof(TunaCurrentState));
        OnPropertyChanged(nameof(TunaSidecarVerifierStatus));
        OnPropertyChanged(nameof(TunaSidecarVerifierDetail));
        OnPropertyChanged(nameof(TunaWalletFileName));
        OnPropertyChanged(nameof(TunaWalletStatus));
        OnPropertyChanged(nameof(TunaWalletAddress));
        OnPropertyChanged(nameof(TunaWalletBalance));
        OnPropertyChanged(nameof(TunaWalletBalanceCategory));
        OnPropertyChanged(nameof(TunaWalletLastVerified));
        OnPropertyChanged(nameof(TunaWalletLastFailure));
        OnPropertyChanged(nameof(IsTunaWalletLinked));
        OnPropertyChanged(nameof(ShowTunaWalletFailure));
        ValidateTunaWalletCommand.NotifyCanExecuteChanged();
        UnlockTunaRuntimeCommand.NotifyCanExecuteChanged();
        CopyTunaWalletAddressCommand.NotifyCanExecuteChanged();
        UnlinkTunaWalletCommand.NotifyCanExecuteChanged();
    }

    private void RefreshTunaRuntimeProperties()
    {
        if (!UiThreadDispatch.CheckAccess())
        {
            _ = UiThreadDispatch.RunAsync(RefreshTunaRuntimePropertiesCore);
            return;
        }

        RefreshTunaRuntimePropertiesCore();
    }

    private void RefreshTunaRuntimePropertiesCore()
    {
        OnPropertyChanged(nameof(TunaRuntimeFlagStatus));
        OnPropertyChanged(nameof(TunaFallbackState));
        OnPropertyChanged(nameof(IsTunaRuntimeEnabled));
        OnPropertyChanged(nameof(IsTunaFileLaneEnabled));
        OnPropertyChanged(nameof(IsTunaScreenLaneEnabled));
        OnPropertyChanged(nameof(TunaMaxPriceNknPerMb));
        OnPropertyChanged(nameof(TunaMaxTotalMiB));
        OnPropertyChanged(nameof(TunaMaxDurationMinutes));
        OnPropertyChanged(nameof(TunaRuntimeStatus));
        OnPropertyChanged(nameof(TunaCurrentState));
        OnPropertyChanged(nameof(TunaStartupTiming));
        OnPropertyChanged(nameof(TunaRuntimeUnlockStatus));
        OnPropertyChanged(nameof(TunaSpendByNLink));
        OnPropertyChanged(nameof(TunaAverageCost));
        OnPropertyChanged(nameof(TunaLastSessionCost));
        OnPropertyChanged(nameof(TunaLastSessionReason));
        UnlockTunaRuntimeCommand.NotifyCanExecuteChanged();
    }

    private TunaRuntimePreferenceState CurrentTunaRuntimePreferences()
        => tunaRuntimePilotService?.Preferences ?? TunaRuntimePreferenceState.Default;

    private bool EffectiveTunaDegradedProviderReadyEnabled()
    {
        if (IsEnabled(ReleaseOverridePolicy.ReadUnsafeEnvironmentVariable(
                TunaRuntimePreferenceState.RequireStrictProviderReadyEnvVar,
                category: "nkn_tuna_provider_readiness")))
        {
            return false;
        }

        return IsEnabled(ReleaseOverridePolicy.ReadUnsafeEnvironmentVariable(
            TunaRuntimePreferenceState.AllowDegradedProviderReadyEnvVar,
            category: "nkn_tuna_provider_readiness"));
    }

    private static bool IsEnabled(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        return normalized.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private TunaUsageAccountingState CurrentTunaUsage()
        => tunaRuntimePilotService?.Usage ?? TunaUsageAccountingState.Empty;

    private TunaRuntimeUnlockState GetTunaRuntimeUnlockState()
    {
        if (tunaRuntimePilotService is null)
        {
            return new TunaRuntimeUnlockState(
                false,
                false,
                false,
                "service_unavailable",
                "Locked",
                "Tuna runtime unavailable.",
                false,
                TimeSpan.Zero);
        }

        try
        {
            return tunaRuntimePilotService.GetUnlockStateAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            return new TunaRuntimeUnlockState(
                false,
                false,
                false,
                tunaRuntimePilotService.RuntimeStatus,
                "Locked",
                "Tuna runtime unavailable.",
                false,
                TimeSpan.Zero);
        }
    }

    private void SaveTunaRuntimePreferences(TunaRuntimePreferenceState state)
    {
        tunaRuntimePilotService?.SavePreferences(state);
        RefreshTunaRuntimeProperties();
    }

    private static string FormatTunaWalletStatus(TunaWalletLinkStatus status, bool validating)
    {
        if (validating)
        {
            return "Validating";
        }

        return status switch
        {
            TunaWalletLinkStatus.Unlinked => "Not linked",
            TunaWalletLinkStatus.LinkedUnverified => "Linked, not verified",
            TunaWalletLinkStatus.VerifiedFunded => "Verified, funded",
            TunaWalletLinkStatus.VerifiedEmpty => "Verified, empty",
            TunaWalletLinkStatus.ValidationFailed => "Validation failed",
            _ => status.ToString(),
        };
    }

    private static string HashForDiagnostics(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(none)";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private void RequestCopyReliabilityLog()
    {
        var text = BuildDiagnosticsCopyText();
        CopyReliabilityLogRequested?.Invoke(this, text);
    }

    private void RequestLinkTunaWallet()
    {
        LinkTunaWalletRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RequestValidateTunaWallet()
    {
        ValidateTunaWalletPasswordRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool CanValidateTunaWallet() => tunaWalletState.IsLinked && !isTunaWalletValidating;

    private void RequestUnlockTunaRuntime()
    {
        UnlockTunaRuntimePasswordRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool CanUnlockTunaRuntime()
    {
        if (tunaRuntimePilotService is null || isTunaWalletValidating)
        {
            return false;
        }

        var state = GetTunaRuntimeUnlockState();
        return state.CanToggle && !state.IsOn;
    }

    private void RequestCopyTunaWalletAddress()
    {
        if (!string.IsNullOrWhiteSpace(tunaWalletState.WalletAddress))
        {
            CopyTunaWalletAddressRequested?.Invoke(this, tunaWalletState.WalletAddress);
        }
    }

    private bool CanCopyTunaWalletAddress()
        => !string.IsNullOrWhiteSpace(tunaWalletState.WalletAddress) && !isTunaWalletValidating;

    private void UnlinkTunaWallet()
    {
        _ = UnlinkTunaWalletAsync();
    }

    internal async Task UnlinkTunaWalletAsync(CancellationToken ct = default)
    {
        try
        {
            if (tunaWalletLinkStore is not null)
            {
                await tunaWalletLinkStore.ClearAsync(ct);
            }

            tunaWalletState = TunaWalletLinkState.Unlinked;
            if (tunaRuntimePilotService is not null)
            {
                await tunaRuntimePilotService.LockOrStopForSessionAsync(
                    "wallet_unlinked",
                    TunaRuntimeUnlockSource.Options,
                    ct).ConfigureAwait(false);
            }

            RefreshTunaWalletProperties();
            RefreshTunaRuntimeProperties();
            copyFeedback.Show("Wallet unlinked");
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn("Diagnostics", $"Tuna wallet unlink failed: {ex.GetType().Name}");
            copyFeedback.Show("Could not unlink wallet");
        }
    }

    private bool CanUnlinkTunaWallet() => tunaWalletState.IsLinked && !isTunaWalletValidating;

    private void RequestOpenLogsFolder()
    {
        OpenLogsFolderRequested?.Invoke(this, LocalOperationalLog.LogsDirectoryPath);
    }

    private void RequestOpenBugReport()
    {
        if (!string.IsNullOrWhiteSpace(bugReportUrl))
        {
            OpenBugReportRequested?.Invoke(this, bugReportUrl);
        }
    }

    private void ApplyBalancedScreenSharePreset()
        => ApplyScreenSharePreset(ScreenShareQualitySettings.BalancedPreset);

    private void ApplyHighQualityScreenSharePreset()
        => ApplyScreenSharePreset(ScreenShareQualitySettings.HighQualityPreset);

    private void ApplyTunaQualityScreenSharePreset()
        => ApplyScreenSharePreset(ScreenShareQualitySettings.TunaQualityPreset);

    private void ApplyHighPerformanceScreenSharePreset()
        => ApplyScreenSharePreset(ScreenShareQualitySettings.HighPerformancePreset);

    private void ApplyScreenSharePreset(ScreenSharePresetDefinition preset)
    {
        var fpsText = preset.CaptureFramesPerSecond.ToString(CultureInfo.InvariantCulture);
        var transportFpsText = preset.TransportFramesPerSecond.ToString(CultureInfo.InvariantCulture);
        var scaleText = preset.CaptureScale.ToString("0.##", CultureInfo.InvariantCulture);
        var profileText = preset.QualityProfile;

        Environment.SetEnvironmentVariable(ScreenShareMaxFpsVariable, fpsText);
        Environment.SetEnvironmentVariable(ScreenShareTransportMaxFpsVariable, transportFpsText);
        Environment.SetEnvironmentVariable(ScreenShareScaleVariable, scaleText);
        Environment.SetEnvironmentVariable(ScreenShareQualityProfileVariable, profileText);

        OnPropertyChanged(nameof(ScreenShareCaptureMaxFps));
        OnPropertyChanged(nameof(ScreenShareTransportMaxFps));
        OnPropertyChanged(nameof(ScreenShareCaptureScale));
        OnPropertyChanged(nameof(ScreenShareQualityProfile));
        OnPropertyChanged(nameof(ScreenShareEffectivePresetName));
        OnPropertyChanged(nameof(ScreenSharePresetMigrationStatus));
        OnPropertyChanged(nameof(AdvancedScreenShareSettingsSummary));
        OnPropertyChanged(nameof(ShowScreenShareResetHint));

        copyFeedback.Show($"{preset.DisplayName} preset applied");
        persistScreenSharePresetInBackground(preset.DisplayName, fpsText, transportFpsText, scaleText, profileText);
    }

    private static void PersistScreenSharePresetInBackground(
        string presetName,
        string fpsText,
        string transportFpsText,
        string scaleText,
        string profileText)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var persisted = TrySetUserEnvironmentVariable(ScreenShareMaxFpsVariable, fpsText) &&
                                TrySetUserEnvironmentVariable(ScreenShareTransportMaxFpsVariable, transportFpsText) &&
                                TrySetUserEnvironmentVariable(ScreenShareScaleVariable, scaleText) &&
                                TrySetUserEnvironmentVariable(ScreenShareQualityProfileVariable, profileText);
                if (!persisted)
                {
                    LocalOperationalLog.Warn("Diagnostics", $"Could not persist ScreenShare preset '{presetName}' to user environment.");
                }
            }
            catch (Exception ex)
            {
                LocalOperationalLog.Warn("Diagnostics", $"Persisting ScreenShare preset '{presetName}' failed: {ex.GetType().Name}");
            }
        });
    }

    private static bool TrySetUserEnvironmentVariable(string name, string value)
    {
        try
        {
            Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SaveHangReport()
    {
        try
        {
            if (hangReportService is null)
            {
                copyFeedback.Show("Hang report unavailable");
                return;
            }

            var result = hangReportService.Capture(
                HangReportTriggerKind.ManualDiagnostics,
                "manual_diagnostics_page",
                diagnosticsTextOverride: BuildDiagnosticsCopyText(),
                screenShareEvidenceTextOverride: BuildScreenShareEvidenceText());
            OpenHangReportFolderRequested?.Invoke(this, result.FolderPath);
            copyFeedback.Show("Hang report saved");
        }
        catch (Exception ex)
        {
            PersistenceDiagnostics.Record(
                domain: "diagnostics_export",
                operation: "save_hang_report",
                severity: PersistenceDiagnosticSeverity.Warning,
                outcome: PersistenceDiagnosticOutcome.Fallback,
                reason: ex.GetType().Name,
                userWarning: "Could not save diagnostics hang report.");
            copyFeedback.Show("Could not save hang report");
        }
    }

    private void ExportMetricsJson()
    {
        try
        {
            var outputPath = ExportMetricsJsonToFile();
            OpenMetricsExportFolderRequested?.Invoke(this, Path.GetDirectoryName(outputPath) ?? diagnosticsExportRootProvider());
            copyFeedback.Show("Metrics exported");
        }
        catch (Exception ex)
        {
            PersistenceDiagnostics.Record(
                domain: "diagnostics_export",
                operation: "export_metrics_json",
                severity: PersistenceDiagnosticSeverity.Warning,
                outcome: PersistenceDiagnosticOutcome.Fallback,
                reason: ex.GetType().Name,
                userWarning: "Could not export diagnostics metrics file.");
            copyFeedback.Show("Could not export metrics");
        }
    }

    public void Dispose()
    {
        if (tunaRuntimePilotService is not null)
        {
            tunaRuntimePilotService.StateChanged -= OnTunaRuntimeStateChanged;
        }

        copyFeedback.Dispose();
    }

    private void OnTunaRuntimeStateChanged(object? sender, EventArgs e)
        => _ = UiThreadDispatch.RunAsync(RefreshTunaRuntimeProperties);

    internal string BuildDiagnosticsCopyTextForTests() => BuildDiagnosticsCopyText();
    internal string BuildScreenShareEvidenceTextForTests() => BuildScreenShareEvidenceText();
    internal string ExportMetricsJsonForTests() => ExportMetricsJsonToFile();

    private string BuildScreenShareEvidenceText()
        => DiagnosticsRedactor.Redact(screenShareEvidenceSnapshot.ToReportText());

    private string BuildDiagnosticsCopyText()
    {
        var metricsSnapshot = metricsRegistry?.Snapshot();
        var timelineText = BuildSessionTimelineText(SessionTimeline.SnapshotRecent(30));
        var tunaAvailability = tunaWalletVerifier?.GetAvailability();
        var tunaProviderReadinessMode = EffectiveTunaDegradedProviderReadyEnabled()
            ? "degraded_allowed"
            : "strict_4_paths";
        var lines = new List<string>
        {
            "Privacy notice",
            "--------------",
            DiagnosticsExportBuilder.BestEffortPrivacyNotice,
            string.Empty,
            "Status",
            "------",
            $"App version: {AppVersion}",
            $"OS: {OsDescription}",
            $"Process architecture: {ProcessArchitecture}",
            $"OS architecture: {OsArchitecture}",
            $"Bridge RID: {BridgeResolutionRid}",
            $"current_state: {CurrentTransportState}",
            $"session_ui_state: {SessionUiState}",
            $"attempt: {AttemptNumber}",
            $"runtime_summary: {RuntimeSummary}",
            $"authorization_summary: {AuthorizationSummary}",
            $"last_authorization_denial_reason: {LastAuthorizationDenialReason}",
            $"session_security_summary: {SessionSecuritySummary}",
            $"remote_control_summary: {RemoteControlSummary}",
            $"screenshare_summary: {ScreenShareSummary}",
            $"file_transfer_summary: {FileTransferSummary}",
            $"file_transfer_inbound_id: {runtimeDiagnosticsSnapshot.ActiveInboundFileTransferId}",
            $"file_transfer_inbound_state: {runtimeDiagnosticsSnapshot.ActiveInboundFileTransferState}",
            $"file_transfer_inbound_bytes: {runtimeDiagnosticsSnapshot.ActiveInboundFileTransferBytes?.ToString() ?? "(none)"}",
            $"file_transfer_outbound_id: {runtimeDiagnosticsSnapshot.ActiveOutboundFileTransferId}",
            $"file_transfer_outbound_state: {runtimeDiagnosticsSnapshot.ActiveOutboundFileTransferState}",
            $"file_transfer_outbound_bytes: {runtimeDiagnosticsSnapshot.ActiveOutboundFileTransferBytes?.ToString() ?? "(none)"}",
            $"file_transfer_last_failure_code: {runtimeDiagnosticsSnapshot.LastFileTransferFailureCode}",
            $"file_transfer_last_saved_path: {DiagnosticsExportBuilder.RedactStructuredValue("file_transfer_last_saved_path", runtimeDiagnosticsSnapshot.LastFileTransferSavedPath)}",
            $"persistence_summary: {PersistenceSummary}",
            $"persistence_warning: {DiagnosticsExportBuilder.RedactStructuredValue("persistence_warning", PersistenceWarning)}",
            string.Empty,
            $"Transport: {TransportSummary}",
            $"Connection method: {ActiveTransport}",
            $"Method code: {TransportKey}",
            $"Build type: {BuildMode}",
            $"App setting: {EnvironmentValue}",
            $"Auto-selected: {AutoSelected}",
            $"Forced by environment: {ForcedByEnvironment}",
            $"Why this was chosen: {SelectionReason}",
            $"Built-in web page view: {EmbeddedWebViewDefault}",
            $"invite_security_mode: {InviteSecurityMode}",
            $"invite_signing_configuration: {InviteSigningConfiguration}",
            $"invite_public_flow: {InvitePublicFlow}",
            $"invite_security_release_ready: {InviteSecurityReleaseReady}",
            $"invite_security_warning: {InviteSecurityWarning}",
            $"security_relevant_overrides: {BuildSecurityRelevantOverridesSummary()}",
            string.Empty,
            "NKN Tuna diagnostics",
            "--------------------",
            $"tuna_runtime_flag: {TunaRuntimeFlagStatus}",
            $"tuna_runtime_status: {TunaRuntimeStatus}",
            $"tuna_startup_timing: {TunaStartupTiming}",
            $"tuna_runtime_unlocked: {TunaRuntimeUnlockStatus}",
            $"tuna_runtime_enabled: {(IsTunaRuntimeEnabled ? "yes" : "no")}",
            $"tuna_runtime_lanes: file={(IsTunaFileLaneEnabled ? "on" : "off")}, screen={(IsTunaScreenLaneEnabled ? "on" : "off")}",
            $"tuna_runtime_caps: max_price_nkn_per_mb={TunaMaxPriceNknPerMb}; max_total_mib={TunaMaxTotalMiB}; max_duration_minutes={TunaMaxDurationMinutes}",
            $"tuna_provider_readiness: {tunaProviderReadinessMode}",
            $"tuna_provider_readiness_env: {DiagnosticsExportBuilder.RedactStructuredValue(TunaRuntimePreferenceState.AllowDegradedProviderReadyEnvVar, Environment.GetEnvironmentVariable(TunaRuntimePreferenceState.AllowDegradedProviderReadyEnvVar))}",
            $"tuna_provider_grace_env: {DiagnosticsExportBuilder.RedactStructuredValue(TunaRuntimePreferenceState.DegradedProviderGraceSecondsEnvVar, Environment.GetEnvironmentVariable(TunaRuntimePreferenceState.DegradedProviderGraceSecondsEnvVar))}",
            $"tuna_fallback_state: {TunaFallbackState}",
            $"tuna_sidecar_verifier: {TunaSidecarVerifierStatus}",
            $"tuna_sidecar_verifier_detail: {TunaSidecarVerifierDetail}",
            $"tuna_sidecar_expected_app_protocol: {NknTunaSidecarCompatibility.AppProtocolVersion}",
            $"tuna_sidecar_expected_frame_protocol: {NknTunaSidecarFrameProtocol.ProtocolVersion}",
            $"tuna_sidecar_expected_version: {NknTunaSidecarCompatibility.ExpectedSidecarVersion}",
            $"tuna_sidecar_actual_app_protocol: {tunaAvailability?.ActualAppProtocolVersion?.ToString(CultureInfo.InvariantCulture) ?? "(none)"}",
            $"tuna_sidecar_actual_frame_protocol: {tunaAvailability?.ActualFrameProtocolVersion?.ToString(CultureInfo.InvariantCulture) ?? "(none)"}",
            $"tuna_sidecar_actual_version: {tunaAvailability?.ActualSidecarVersion ?? "(none)"}",
            $"tuna_sidecar_actual_runtime: {tunaAvailability?.ActualRuntime ?? "(none)"}",
            $"tuna_sidecar_manifest_status: {tunaAvailability?.ManifestStatus ?? "(none)"}",
            $"tuna_sidecar_path: {DiagnosticsExportBuilder.RedactStructuredValue("tuna_sidecar_path", tunaAvailability?.SidecarPath)}",
            $"tuna_sidecar_manifest_path: {DiagnosticsExportBuilder.RedactStructuredValue("tuna_sidecar_manifest_path", tunaAvailability?.ManifestPath)}",
            $"tuna_wallet_file: {TunaWalletFileName}",
            $"tuna_wallet_status: {TunaWalletStatus}",
            $"tuna_wallet_balance_category: {TunaWalletBalanceCategory}",
            $"tuna_wallet_last_verified_utc: {TunaWalletLastVerified}",
            $"tuna_spend_by_nlink: {TunaSpendByNLink}",
            $"tuna_average_cost: {TunaAverageCost}",
            $"tuna_last_session_cost: {TunaLastSessionCost}",
            $"tuna_last_session_reason: {TunaLastSessionReason}",
            $"tuna_last_session_payment_status: {CurrentTunaUsage().LastSessionRecord?.PaymentTelemetryStatus ?? "(none)"}",
            $"tuna_last_session_run_id_hash: {HashForDiagnostics(CurrentTunaUsage().LastSessionRecord?.SessionRunId)}",
            $"tuna_last_session_bytes: {CurrentTunaUsage().LastSessionRecord?.BytesMoved.ToString(CultureInfo.InvariantCulture) ?? "0"}",
            $"tuna_last_session_mb: {FormatDecimal(CurrentTunaUsage().LastSessionRecord?.AppPayloadMb ?? 0m, 6)}",
            $"tuna_last_session_paid_nkn: {FormatDecimal(CurrentTunaUsage().LastSessionRecord?.PaidNkn ?? 0m, 8)}",
            $"tuna_last_session_average_nkn_per_mb: {FormatDecimal(CurrentTunaUsage().LastSessionRecord?.AverageNknPerMb ?? 0m, 9)}",
            $"tuna_last_session_payment_event_count: {CurrentTunaUsage().LastSessionRecord?.PaymentEventCount.ToString(CultureInfo.InvariantCulture) ?? "0"}",
            $"tuna_last_session_cap_reason: {CurrentTunaUsage().LastSessionRecord?.CapReason ?? string.Empty}",
            $"tuna_last_session_fallback_reason: {CurrentTunaUsage().LastSessionRecord?.FallbackReason ?? string.Empty}",
            $"tuna_last_session_completed_from_summary: {(CurrentTunaUsage().LastSessionRecord?.CompletedFromSummary == true ? "yes" : "no")}",
            $"tuna_wallet_address_hash: {HashForDiagnostics(tunaWalletState.WalletAddress)}",
            $"tuna_wallet_path: {DiagnosticsExportBuilder.RedactStructuredValue("tuna_wallet_path", tunaWalletState.WalletPath)}",
            $"tuna_wallet_address: {DiagnosticsExportBuilder.RedactStructuredValue("tuna_wallet_address", tunaWalletState.WalletAddress)}",
            $"tuna_wallet_last_failure: {DiagnosticsExportBuilder.RedactStructuredValue("tuna_wallet_last_failure", tunaWalletState.LastFailureReason)}",
            string.Empty,
            $"screenshare_capture_max_fps: {FeatureFlags.ScreenShareMaxFps}",
            $"screenshare_transport_max_fps: {FeatureFlags.ScreenShareTransportMaxFps}",
            $"screenshare_transport_autotune: {FormatFeatureFlag(FeatureFlags.ScreenShareTransportAutoTuneEnabled)}",
            $"screenshare_capture_scale: {FeatureFlags.ScreenShareScale:0.###}",
            $"screenshare_quality_profile: {FeatureFlags.ScreenShareQualityProfile}",
            $"screenshare_effective_preset: {ScreenShareEffectivePresetName}",
            $"screenshare_legacy_preset_migrated: {ScreenSharePresetMigrationStatus}",
            string.Empty,
            "screenshare_capture_presets:",
            $"  balanced_default: {ScreenShareQualitySettings.BalancedPreset.Describe()}",
            $"  high_quality: {ScreenShareQualitySettings.HighQualityPreset.Describe()}",
            $"  tuna_quality: {ScreenShareQualitySettings.TunaQualityPreset.Describe()}",
            $"  high_performance: {ScreenShareQualitySettings.HighPerformancePreset.Describe()}",
            $"  apply_hint: {ScreenShareCaptureEnvHint}",
            string.Empty,
            BuildScreenShareEvidenceText(),
            string.Empty,
            "Bridge / NKN",
            "------------",
            $"Bridge PID: {BridgePid}",
            $"Node/SDK: {NodeSdk}",
            $"authoritative_connected_address: {AuthoritativeConnectedAddress}",
            $"Last heartbeat: {LastHeartbeat}",
            $"Bridge restarts: {BridgeRestarts}",
            $"Last bridge exit: {LastBridgeExit}",
            $"bridge_process_status: {BuildBridgeProcessStatus()}",
            $"bridge_manifest_summary: {BridgeManifestSummary}",
            $"bridge_raw_messages_received: {BridgeRawMessagesReceived}",
            $"screenshare_outbound_busy_drops: {ScreenShareOutboundBusyDrops}",
            $"screenshare_messages_sent: {ScreenShareMessagesSent}",
            $"screenshare_payload_bytes_sent: {ScreenSharePayloadBytesSent}",
            $"screenshare_bridge_bytes_sent: {ScreenShareBridgeBytesSent}",
            $"high_priority_control_queue_overflows: {HighPriorityControlQueueOverflows}",
            $"high_priority_control_rejected: {HighPriorityControlRejected}",
            $"high_priority_control_coalesced: {HighPriorityControlCoalesced}",
            $"high_priority_control_dropped_for_stop: {HighPriorityControlDroppedForStop}",
            $"last_bridge_message_kind: {LastBridgeMessageKind}",
            $"last_envelope_type: {LastEnvelopeType}",
            $"last_envelope_drop_reason: {LastEnvelopeDropReason}",
            $"last_rejected_message: {LastRejectedMessageSummary}",
            $"helper_address_source: {HelperAddressSource}",
            $"helper_address_authoritative: {HelperAddressAuthoritative}",
            $"helper_verification_code_visible: {HelperVerificationCodeVisible}",
            $"helper_identity_regenerated_count: {HelperIdentityRegeneratedCount}",
            $"helper_identity_last_regenerated_utc: {HelperIdentityLastRegeneratedUtc}",
            $"join_requests_received: {JoinRequestsReceived}",
            $"incoming_join_request_raised: {IncomingJoinRequestRaisedCount}",
            $"acks_received: {AcksReceived}",
            $"acks_ignored_source_mismatch: {AcksIgnoredSourceMismatch}",
            $"last_disconnect_reason: {DiagnosticsExportBuilder.RedactStructuredValue("last_disconnect_reason", LastDisconnectReason)}",
            $"bridge_first_cold_start_observed: {FirstColdStartObserved}",
            $"bridge_first_cold_start_ms: {FirstColdStartMs}",
            $"bridge_first_cold_start_recorded_utc: {FirstColdStartRecordedUtc}",
            string.Empty,
            "Counters",
            "--------",
            $"messages_sent: {MessagesSent}",
            $"messages_received: {MessagesReceived}",
            $"chat_sent: {ChatSent}",
            $"chat_received: {ChatReceived}",
            $"decrypt_failed: {DecryptFailed}",
            $"last_connect_duration_ms: {LastConnectDurationMs}",
            $"last_handshake_duration_ms: {LastHandshakeDurationMs}",
            $"last_bridge_start_ms: {LastBridgeStartDurationMs}",
            string.Empty,
            "Metrics snapshot",
            "--------------",
            BuildCompactMetricsSummary(
                metricsSnapshot,
                LastConnectDurationMs,
                LastHandshakeDurationMs,
                LastBridgeStartDurationMs),
            string.Empty,
            "Resource snapshot",
            "--------------",
            BuildCompactResourceSummary(),
            string.Empty,
            "Errors",
            "------",
            $"last_failure_category: {LastFailureCategory}",
            $"last_failure_message: {DiagnosticsExportBuilder.RedactStructuredValue("last_failure_message", LastFailureMessage)}",
            $"last_error: {DiagnosticsExportBuilder.RedactStructuredValue("last_error", LastError)}",
            string.Empty,
            "Persistence diagnostics",
            "-----------------------",
            BuildPersistenceDiagnosticsSummary(),
            string.Empty,
            "Session timeline (last 30)",
            "----------------------",
            timelineText,
            string.Empty,
            $"{RecentConnectionAttemptsTitle}:",
            RecentConnectionAttemptsText
        };

        return DiagnosticsRedactor.Redact(string.Join(Environment.NewLine, lines));
    }

    private string BuildPersistenceDiagnosticsSummary()
    {
        var lines = new List<string>
        {
            $"summary: {PersistenceSummary}",
            $"warning_count: {persistenceDiagnosticsSnapshot.WarningCount}",
            $"error_count: {persistenceDiagnosticsSnapshot.ErrorCount}",
            $"last_warning: {DiagnosticsExportBuilder.RedactStructuredValue("persistence_warning", PersistenceWarning)}",
        };

        if (persistenceDiagnosticsSnapshot.RecentEvents.Count == 0)
        {
            lines.Add("recent_events: (none)");
            return string.Join(Environment.NewLine, lines);
        }

        lines.Add("recent_events:");
        foreach (var entry in persistenceDiagnosticsSnapshot.RecentEvents)
        {
            lines.Add($"  {entry.TimestampUtc:u} | {entry.Domain} | {entry.Operation} | {entry.Severity} | {entry.Outcome} | {entry.Reason}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private string BuildCompactResourceSummary()
    {
        var last = resourceRuntimeTracker?.GetLastSnapshot();
        var peak = resourceRuntimeTracker?.GetPeakSnapshot();
        var latestResourceSummary = resourceRuntimeTracker?.TryReadLatestResourceSummary();
        var latestLeakSummary = resourceRuntimeTracker?.TryReadLatestLeakCheckSummary();

        var lines = new List<string>();
        if (last is null)
        {
            lines.Add("last_snapshot: (none)");
        }
        else
        {
            lines.Add($"last_snapshot_utc: {last.TimestampUtc:u}");
            lines.Add($"app_last_working_set_mb: {last.App.WorkingSetMB:F2}");
            lines.Add($"app_last_private_bytes_mb: {last.App.PrivateBytesMB:F2}");
            lines.Add($"app_last_threads: {last.App.ThreadCount}");
            lines.Add($"app_last_handles: {last.App.HandleCount}");
            lines.Add($"app_last_cpu_pct: {last.App.CpuPercent:F2}");
            if (last.Bridge is not null)
            {
                lines.Add($"bridge_last_working_set_mb: {last.Bridge.WorkingSetMB:F2}");
                lines.Add($"bridge_last_private_bytes_mb: {last.Bridge.PrivateBytesMB:F2}");
                lines.Add($"bridge_last_threads: {last.Bridge.ThreadCount}");
                lines.Add($"bridge_last_handles: {last.Bridge.HandleCount}");
                lines.Add($"bridge_last_cpu_pct: {last.Bridge.CpuPercent:F2}");
            }
            else
            {
                lines.Add("bridge_last_snapshot: (not running)");
            }

            lines.Add($"active_sessions: {last.ActiveCounters.ActiveSessions}");
            lines.Add($"active_connect_attempts: {last.ActiveCounters.ActiveConnectAttempts}");
            lines.Add($"active_retry_timers: {last.ActiveCounters.ActiveRetryTimers}");
            lines.Add($"active_watchdogs: {last.ActiveCounters.ActiveWatchdogs}");
            lines.Add($"active_transport_tasks: {last.ActiveCounters.ActiveTransportTasks}");
            lines.Add($"active_bridge_io_readers: {last.ActiveCounters.ActiveBridgeIoReaders}");
        }

        if (peak is not null)
        {
            lines.Add($"app_peak_working_set_mb_since_start: {peak.App.WorkingSetMB:F2}");
            lines.Add($"app_peak_private_bytes_mb_since_start: {peak.App.PrivateBytesMB:F2}");
            if (peak.Bridge is not null)
            {
                lines.Add($"bridge_peak_working_set_mb_since_start: {peak.Bridge.WorkingSetMB:F2}");
                lines.Add($"bridge_peak_private_bytes_mb_since_start: {peak.Bridge.PrivateBytesMB:F2}");
            }
        }

        if (!string.IsNullOrWhiteSpace(latestResourceSummary))
        {
            lines.Add(string.Empty);
            lines.Add("last_resource_benchmark_summary:");
            lines.AddRange(TrimSummaryLines(latestResourceSummary!, 12));
        }

        if (!string.IsNullOrWhiteSpace(latestLeakSummary))
        {
            lines.Add(string.Empty);
            lines.Add("last_leak_check_summary:");
            lines.AddRange(TrimSummaryLines(latestLeakSummary!, 12));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildSecurityRelevantOverridesSummary()
    {
        var riskyOverrides = new List<string>();

        if (ReleaseOverridePolicy.UnsafeDeveloperModeEnabled)
        {
            riskyOverrides.Add("unsafe_developer_mode=on");
        }

        riskyOverrides.AddRange(ReleaseOverridePolicy.GetSuppressedOverrideSummaries());

        if (!FeatureFlags.RemoteControlSeqGateEnabled)
        {
            riskyOverrides.Add("remote_control_seq_gate=off");
        }

        var nknOptions = NknTransportOptions.Load();
        if (nknOptions.PreflightRpcEnabled)
        {
            riskyOverrides.Add("nkn_preflight_rpc=on");
        }

        if (NknTunaAccelerationOptions.Load().Enabled)
        {
            riskyOverrides.Add("nkn_tuna=on");
        }

        AddIfNonDefault(
            riskyOverrides,
            "screenshare_capture_max_fps",
            FeatureFlags.ScreenShareMaxFps,
            15,
            ScreenShareMaxFpsVariable);
        AddIfNonDefault(
            riskyOverrides,
            "screenshare_transport_max_fps",
            FeatureFlags.ScreenShareTransportMaxFps,
            8,
            ScreenShareTransportMaxFpsVariable);
        AddIfNonDefault(
            riskyOverrides,
            "screenshare_capture_scale",
            FeatureFlags.ScreenShareScale.ToString("0.###", CultureInfo.InvariantCulture),
            1d.ToString("0.###", CultureInfo.InvariantCulture),
            ScreenShareScaleVariable);
        AddIfNonDefault(
            riskyOverrides,
            "screenshare_quality_profile",
            FeatureFlags.ScreenShareQualityProfile,
            FeatureFlags.ScreenShareQualityProfileNormal,
            ScreenShareQualityProfileVariable);
        if (!FeatureFlags.ScreenShareTransportAutoTuneEnabled &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ScreenShareTransportAutotuneVariable)))
        {
            riskyOverrides.Add("screenshare_transport_autotune=off");
        }

        return riskyOverrides.Count == 0 ? "none" : string.Join(", ", riskyOverrides);
    }

    private string BuildLastRejectedMessageSummary()
    {
        var envelopeType = string.IsNullOrWhiteSpace(LastEnvelopeType) ? "(none)" : LastEnvelopeType;
        var dropReason = string.IsNullOrWhiteSpace(LastEnvelopeDropReason) ? "(none)" : LastEnvelopeDropReason;
        var error = string.IsNullOrWhiteSpace(LastError) ? "(none)" : LastError;
        return $"envelope={envelopeType}; reason={dropReason}; error={error}";
    }

    private string BuildConnectionErrorSummary()
    {
        var lastDisconnect = string.IsNullOrWhiteSpace(LastDisconnectReason) ? "(none)" : LastDisconnectReason;
        var lastFailure = string.IsNullOrWhiteSpace(LastFailureCategory) ? "(none)" : LastFailureCategory;
        var lastError = string.IsNullOrWhiteSpace(LastError) ? "(none)" : LastError;
        return $"last_disconnect={lastDisconnect}; last_failure={lastFailure}; last_error={lastError}";
    }

    private string BuildHelperIdentitySummary()
        => $"source={HelperAddressSource}; authoritative={HelperAddressAuthoritative}; verification_code_visible={HelperVerificationCodeVisible}; regenerated={HelperIdentityRegeneratedCount}; last_regenerated={HelperIdentityLastRegeneratedUtc}";

    private string BuildScreenShareLiveProfileSummary()
    {
        if (!screenShareLiveSnapshot.AnyScreenShareActive &&
            screenShareLiveSnapshot.ActiveTargetWidth <= 0 &&
            screenShareLiveSnapshot.ActiveTargetHeight <= 0)
        {
            return "(none)";
        }

        var target = screenShareLiveSnapshot.ActiveTargetWidth > 0 && screenShareLiveSnapshot.ActiveTargetHeight > 0
            ? $"{screenShareLiveSnapshot.ActiveTargetWidth}x{screenShareLiveSnapshot.ActiveTargetHeight}"
            : "(unknown)";
        var fps = screenShareLiveSnapshot.ActiveTargetFramesPerSecond > 0
            ? screenShareLiveSnapshot.ActiveTargetFramesPerSecond.ToString(CultureInfo.InvariantCulture)
            : "(unknown)";
        var bitrate = screenShareLiveSnapshot.ActiveTargetBitrate > 0
            ? screenShareLiveSnapshot.ActiveTargetBitrate.ToString(CultureInfo.InvariantCulture)
            : "(unknown)";

        return $"mode={screenShareLiveSnapshot.SenderMode}; operating={screenShareLiveSnapshot.SenderOperatingState}; target={target}@{fps}fps; bitrate={bitrate}; blocker={screenShareLiveSnapshot.DominantPressureBlocker}";
    }

    private string BuildScreenShareLivePerformanceSummary()
        => $"encoded_fps={FormatOptionalDouble(screenShareLiveSnapshot.ActualEncodedDisplayableFps)}; readback_fps={FormatOptionalDouble(screenShareLiveSnapshot.RawSourceReadbackFps)}; readback_size={FormatSize(screenShareLiveSnapshot.RawSourceOutputWidth, screenShareLiveSnapshot.RawSourceOutputHeight)}; gpu_scale={FormatYesNo(screenShareLiveSnapshot.RawSourceGpuScaleEnabled)}; gpu_fallback={FormatValue(screenShareLiveSnapshot.RawSourceGpuScaleFallbackReason)}; wgc_active={FormatYesNo(screenShareLiveSnapshot.RawSourceCaptureActive)}; border={FormatYesNo(screenShareLiveSnapshot.RawSourceBorderRequired)}; border_status={FormatValue(screenShareLiveSnapshot.RawSourceBorderRequiredApplyStatus)}; last_stop_ms={FormatOptionalLong(screenShareLiveSnapshot.RawSourceLastStopDurationMs)}; wgc_leases={screenShareLiveSnapshot.RawSourceActiveSessionLeaseCount}; close_status={FormatValue(screenShareLiveSnapshot.RawSourceLastSessionCloseStatus)}; close_method={FormatValue(screenShareLiveSnapshot.RawSourceLastSessionCloseMethod)}; owner_thread={screenShareLiveSnapshot.RawSourceSessionOwnerThreadId}; close_thread={screenShareLiveSnapshot.RawSourceLastSessionCloseThreadId}; close_on_owner={FormatYesNo(screenShareLiveSnapshot.RawSourceLastSessionCloseOnOwnerThread)}; owner_active={FormatYesNo(screenShareLiveSnapshot.RawSourceOwnerDispatcherActive)}; close_timeouts={screenShareLiveSnapshot.RawSourceOwnerThreadCloseTimeoutCount}; close_anomalies={screenShareLiveSnapshot.RawSourceSessionCloseAnomalyCount}";

    private string BuildScreenShareLiveCpuSummary()
        => $"sender_cpu_pct={FormatOptionalDouble(screenShareLiveSnapshot.SenderProcessCpuPercent)}; preprocess_ms={FormatOptionalLong(screenShareLiveSnapshot.LastPreprocessDurationMs)}; resize_ms={FormatOptionalLong(screenShareLiveSnapshot.LastPreprocessResizeDurationMs)}; color_ms={FormatOptionalLong(screenShareLiveSnapshot.LastPreprocessColorConvertDurationMs)}; path={FormatValue(screenShareLiveSnapshot.PreprocessResizePath)}";

    private string BuildScreenShareLiveCursorSummary()
        => $"mode={FormatValue(screenShareLiveSnapshot.CursorDeliveryMode)}; capture_desired={FormatYesNo(screenShareLiveSnapshot.CursorCaptureDesiredEnabled)}; capture_enabled={FormatYesNo(screenShareLiveSnapshot.CursorCaptureEnabled)}; control_supported={FormatYesNo(screenShareLiveSnapshot.CursorCaptureControlSupported)}; status={FormatValue(screenShareLiveSnapshot.CursorCaptureApplyStatus)}; fallback={FormatValue(screenShareLiveSnapshot.CursorCaptureFallbackReason)}";

    private string BuildScreenShareLiveVisualSafetySummary()
        => $"unsafe_tail={screenShareLiveSnapshot.PreCandidateGapTailEmittedToViewerCount}; actionable_late={screenShareLiveSnapshot.ActionableLateFragmentCount}; h264_taint={(screenShareLiveSnapshot.H264ReferenceTaintActive ? 1 : 0)}; h264_quarantine={(screenShareLiveSnapshot.H264ReferenceQuarantineActive ? 1 : 0)}; taint_enter={screenShareLiveSnapshot.H264ReferenceTaintEnterCount}; taint_release={screenShareLiveSnapshot.H264ReferenceTaintReleaseCount}; reason={FormatValue(screenShareLiveSnapshot.H264ReferenceTaintLastReason)}";

    private string BuildAdvancedScreenShareSettingsSummary()
    {
        var currentState = ScreenShareQualitySettings.GetCurrentEnvironmentState();
        var preset = ScreenShareQualitySettings.ResolvePresetDefinition(currentState.EffectivePresetKey);
        var maxTransportTarget = preset is { } resolvedPreset
            ? $"{resolvedPreset.MaxTransportWidth}x{resolvedPreset.MaxTransportHeight}"
            : "(custom)";

        var scalePercent = (FeatureFlags.ScreenShareScale * 100d).ToString("0", CultureInfo.InvariantCulture) + "%";
        return $"Current preset: {ScreenShareEffectivePresetName}. Capture {ScreenShareCaptureMaxFps} FPS, send {ScreenShareTransportMaxFps} FPS, resolution up to {maxTransportTarget}, scale {scalePercent}.";
    }

    private static string FormatOptionalDouble(double value)
        => value >= 0 ? value.ToString("F2", CultureInfo.InvariantCulture) : "(none)";

    private static string FormatNkn(decimal value)
        => $"{FormatDecimal(value, 8)} NKN";

    private static string FormatTunaSpend(TunaUsageAccountingState usage)
    {
        if (usage.HasPaymentTelemetryGaps)
        {
            return usage.TotalPaidNkn > 0m
                ? $"{FormatNkn(usage.TotalPaidNkn)} (some sessions missing payment telemetry)"
                : "no payment telemetry reported";
        }

        return FormatNkn(usage.TotalPaidNkn);
    }

    private static string FormatTunaAverageCost(TunaUsageAccountingState usage)
    {
        if (usage.TotalAppPayloadMb <= 0m)
        {
            return "(none)";
        }

        if (usage.TotalKnownAppPayloadMb <= 0m)
        {
            return "no payment telemetry reported";
        }

        var suffix = usage.HasPaymentTelemetryGaps ? " (known sessions only)" : string.Empty;
        return $"{FormatDecimal(usage.AverageNknPerMb, 9)} NKN/MB{suffix}";
    }

    private static string FormatTunaLastSessionCost(TunaUsageAccountingState usage)
    {
        var record = usage.LastSessionRecord;
        if (record is not null)
        {
            if (record.PaidNkn <= 0m && record.AppPayloadMb <= 0m)
            {
                return "(none)";
            }

            if (string.Equals(record.PaymentTelemetryStatus, TunaPaymentTelemetryStatus.Reported, StringComparison.Ordinal))
            {
                return $"{FormatNkn(record.PaidNkn)} over {FormatDecimal(record.AppPayloadMb, 2)} MB";
            }

            return $"{FormatPaymentTelemetryStatus(record.PaymentTelemetryStatus)} over {FormatDecimal(record.AppPayloadMb, 2)} MB";
        }

        if (usage.LastSessionPaidNkn <= 0m && usage.LastSessionAppPayloadMb <= 0m)
        {
            return "(none)";
        }

        if (usage.LastSessionCostUnknown)
        {
            return $"no payment telemetry reported over {FormatDecimal(usage.LastSessionAppPayloadMb, 2)} MB";
        }

        return $"{FormatNkn(usage.LastSessionPaidNkn)} over {FormatDecimal(usage.LastSessionAppPayloadMb, 2)} MB";
    }

    private static string FormatTunaLastSessionReason(TunaUsageAccountingState usage)
    {
        var record = usage.LastSessionRecord;
        if (record is null)
        {
            return "(none)";
        }

        if (string.Equals(record.CapReason, "byte_cap_reached", StringComparison.Ordinal))
        {
            return "byte cap reached";
        }

        if (string.Equals(record.CapReason, "duration_cap_reached", StringComparison.Ordinal))
        {
            return "duration cap reached";
        }

        if (!string.IsNullOrWhiteSpace(record.FallbackReason))
        {
            return FriendlyTunaReason(record.FallbackReason);
        }

        if (!string.IsNullOrWhiteSpace(record.StopReason))
        {
            return FriendlyTunaReason(record.StopReason);
        }

        return record.CompletedFromSummary ? "fallback to NKN" : "sidecar exited before summary";
    }

    private static string FormatPaymentTelemetryStatus(string? status)
        => status switch
        {
            TunaPaymentTelemetryStatus.Reported => "payment telemetry reported",
            TunaPaymentTelemetryStatus.AccountingIncomplete => "accounting incomplete",
            TunaPaymentTelemetryStatus.None => "(none)",
            _ => "no payment telemetry reported",
        };

    private static string FriendlyTunaReason(string reason)
    {
        var normalized = string.IsNullOrWhiteSpace(reason) ? string.Empty : reason.Trim();
        return normalized switch
        {
            "byte_cap_reached" => "byte cap reached",
            "duration_cap_reached" => "duration cap reached",
            "user_disabled" or "user_locked" or "test_lock" => "user stopped Tuna",
            "sidecar_exited_before_summary" => "sidecar exited before summary",
            "listener_ready_timeout" => "listener ready timeout",
            "listener_failed" or "listener_start_failed" => "listener failed",
            "context_deadline_exceeded" => "duration cap reached",
            "" => "(none)",
            _ when normalized.Contains("deadline", StringComparison.OrdinalIgnoreCase) => "duration cap reached",
            _ when normalized.Contains("cap", StringComparison.OrdinalIgnoreCase) => "cap reached",
            _ when normalized.Contains("closed", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Contains("eof", StringComparison.OrdinalIgnoreCase) => "fallback to NKN",
            _ => normalized.Replace('_', ' '),
        };
    }

    private static string FormatDecimal(decimal value, int precision)
    {
        var rounded = Math.Round(Math.Max(0m, value), precision, MidpointRounding.AwayFromZero);
        var text = rounded.ToString("0." + new string('#', precision), CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(text) ? "0" : text;
    }

    private static string FormatOptionalLong(long value)
        => value >= 0 ? value.ToString(CultureInfo.InvariantCulture) : "(none)";

    private static string FormatSize(int width, int height)
        => width > 0 && height > 0 ? $"{width}x{height}" : "(none)";

    private static string FormatYesNo(bool value) => value ? "Yes" : "No";

    private static string FormatValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? "(none)" : value.Trim();

    private static void AddIfNonDefault<T>(
        List<string> riskyOverrides,
        string key,
        T currentValue,
        T defaultValue,
        string envVarName)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(currentValue, defaultValue) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envVarName)))
        {
            riskyOverrides.Add($"{key}={currentValue}");
        }
    }

    private static IEnumerable<string> TrimSummaryLines(string text, int maxLines)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length && i < maxLines; i++)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]))
            {
                yield return lines[i];
            }
        }

        if (lines.Length > maxLines)
        {
            yield return "...";
        }
    }

    private string ExportMetricsJsonToFile()
    {
        if (metricsRegistry is null)
        {
            throw new InvalidOperationException("Metrics registry is not available.");
        }

        var root = diagnosticsExportRootProvider();
        var timestamp = nowProvider().UtcDateTime.ToString("yyyyMMdd-HHmmss");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, $"metrics-{timestamp}.json");
        File.WriteAllText(path, metricsRegistry.ExportJson(indented: true));
        return Path.GetFullPath(path);
    }

    private static string BuildCompactMetricsSummary(
        MetricsSnapshot? snapshot,
        string lastConnectDurationMs,
        string lastHandshakeDurationMs,
        string lastBridgeStartDurationMs)
    {
        if (snapshot is null)
        {
            return "Metrics not available.";
        }

        long SumCounter(string name) => snapshot.Counters.Where(c => c.Name == name).Sum(c => c.Value);

        var connectAttempts = SumCounter("transport_connect_attempts_total");
        var connectSuccess = SumCounter("transport_connect_success_total");
        var connectFailure = SumCounter("transport_connect_failure_total");
        var reconnectAttempts = SumCounter("transport_reconnect_attempts_total");
        var bridgeStarts = SumCounter("bridge_start_total");
        var bridgeRestarts = SumCounter("bridge_restart_total");
        var bridgeCrashes = SumCounter("bridge_crash_total");

        var successRate = connectAttempts > 0
            ? (double)connectSuccess / connectAttempts * 100.0
            : 0.0;

        var lines = new List<string>
        {
            $"connect_attempts_total: {connectAttempts}",
            $"connect_success_total: {connectSuccess}",
            $"connect_failure_total: {connectFailure}",
            $"connect_success_rate_pct: {successRate:F1}",
            $"reconnect_attempts_total: {reconnectAttempts}",
            $"bridge_start_total: {bridgeStarts}",
            $"bridge_restart_total: {bridgeRestarts}",
            $"bridge_crash_total: {bridgeCrashes}",
            $"last_connect_duration_ms: {lastConnectDurationMs}",
            $"last_handshake_duration_ms: {lastHandshakeDurationMs}",
            $"last_bridge_start_ms: {lastBridgeStartDurationMs}",
        };

        AppendHistogramSummary(lines, snapshot, "transport_connect_duration_ms");
        AppendHistogramSummary(lines, snapshot, "transport_handshake_duration_ms");
        AppendHistogramSummary(lines, snapshot, "bridge_start_duration_ms");
        AppendGaugeSummary(lines, snapshot, "bridge_cold_start_ms");

        return string.Join(Environment.NewLine, lines);
    }

    private static void AppendGaugeSummary(List<string> lines, MetricsSnapshot snapshot, string gaugeName)
    {
        var entries = snapshot.Gauges.Where(g => g.Name == gaugeName).ToArray();
        if (entries.Length == 0)
        {
            lines.Add($"{gaugeName}: (none)");
            return;
        }

        var value = entries.Max(g => g.Value);
        lines.Add($"{gaugeName}: {value:F2}");
    }

    private static void AppendHistogramSummary(List<string> lines, MetricsSnapshot snapshot, string histogramName)
    {
        var entries = snapshot.Histograms.Where(h => h.Name == histogramName).ToArray();
        if (entries.Length == 0)
        {
            lines.Add($"{histogramName}: (none)");
            return;
        }

        var count = entries.Sum(h => h.Count);
        var sum = entries.Sum(h => h.Sum);
        var min = entries.Where(h => h.Count > 0).Select(h => h.Min).DefaultIfEmpty(0).Min();
        var max = entries.Where(h => h.Count > 0).Select(h => h.Max).DefaultIfEmpty(0).Max();
        var mean = count > 0 ? sum / count : 0;
        var p50 = EstimatePercentile(entries, 0.50);
        var p95 = EstimatePercentile(entries, 0.95);

        lines.Add($"{histogramName}: count={count}, min={min:F2}, max={max:F2}, mean={mean:F2}, p50={p50:F2}, p95={p95:F2}");
    }

    private static double EstimatePercentile(IReadOnlyList<HistogramMetricSnapshot> entries, double percentile)
    {
        var allBuckets = new SortedDictionary<double, long>();
        long total = 0;

        foreach (var entry in entries)
        {
            foreach (var bucket in entry.Buckets)
            {
                if (bucket.Count <= 0)
                {
                    continue;
                }

                total += bucket.Count;
                var key = double.IsPositiveInfinity(bucket.UpperBound) ? double.MaxValue : bucket.UpperBound;
                allBuckets.TryGetValue(key, out var existing);
                allBuckets[key] = existing + bucket.Count;
            }
        }

        if (total == 0 || allBuckets.Count == 0)
        {
            return 0;
        }

        var threshold = (long)Math.Ceiling(total * percentile);
        long running = 0;
        foreach (var pair in allBuckets)
        {
            running += pair.Value;
            if (running >= threshold)
            {
                return pair.Key == double.MaxValue ? 0 : pair.Key;
            }
        }

        return 0;
    }

    private static string BuildRecentConnectionAttemptsText(IReadOnlyList<SessionReliabilityRecord> rows)
    {
        if (rows.Count == 0)
        {
            return "No recent entries yet.";
        }

        var lines = new List<string>(rows.Count);
        foreach (var row in rows)
        {
            var result = string.Equals(row.Stage, SessionReliabilityStage.Completed.ToString(), StringComparison.Ordinal)
                ? "Completed"
                : (string.IsNullOrWhiteSpace(row.ErrorCode) ? "In progress" : "Failed");

            var line = $"{row.TimestampUtc:HH:mm:ss} | {row.Mode} | {result} | {row.Stage}";
            if (!string.IsNullOrWhiteSpace(row.ErrorCode))
            {
                line += $" | {row.ErrorCode}";
            }

            lines.Add(line);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildSessionTimelineText(IReadOnlyList<SessionTimelineEntry> rows)
    {
        if (rows.Count == 0)
        {
            return "No session events yet.";
        }

        var lines = new List<string>(rows.Count);
        foreach (var row in rows)
        {
            var line = $"{row.TimestampUtc:HH:mm:ss} | {row.EventName}";
            if (!string.IsNullOrWhiteSpace(row.Reason))
            {
                line += $" | {row.Reason}";
            }

            lines.Add(line);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildBridgeMessageKind(bool? isTopic)
    {
        return isTopic switch
        {
            true => "topic",
            false => "direct",
            null => "(none)"
        };
    }

    private string BuildBridgeProcessStatus()
    {
        if (BridgePid != "(not running)")
        {
            return $"running (pid {BridgePid})";
        }

        return $"not running (last exit: {LastBridgeExit})";
    }

    private string BuildBridgeManifestSummary()
    {
        var version = nknDiagnosticsSnapshot.BridgeManifestVersion > 0
            ? nknDiagnosticsSnapshot.BridgeManifestVersion.ToString(CultureInfo.InvariantCulture)
            : "(none)";
        return $"status={nknDiagnosticsSnapshot.BridgeManifestStatus}; " +
               $"reason={nknDiagnosticsSnapshot.BridgeManifestReason}; " +
               $"version={version}; " +
               $"script_hash_prefix={nknDiagnosticsSnapshot.BridgeManifestHashPrefix}; " +
               $"owner_pid_watchdog={FormatYesNo(nknDiagnosticsSnapshot.BridgeManifestOwnerPidWatchdog)}; " +
               $"kill_on_close_job={FormatYesNo(nknDiagnosticsSnapshot.BridgeManifestKillOnCloseJob)}";
    }

    private static string FormatDuration(double? value)
    {
        return value.HasValue ? value.Value.ToString("F2") : "(none)";
    }

    private static string ResolveAppVersion()
    {
        try
        {
            var assembly = typeof(DiagnosticsPageViewModel).Assembly;
            var info = assembly.GetCustomAttributes<AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                return info!;
            }

            return assembly.GetName().Version?.ToString() ?? "(unknown)";
        }
        catch
        {
            return "(unknown)";
        }
    }

    private static string ResolveBridgeRidForDiagnostics()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                RuntimeInformation.OSArchitecture == Architecture.X64)
            {
                return "win-x64";
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
                RuntimeInformation.OSArchitecture == Architecture.X64)
            {
                return "linux-x64";
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return RuntimeInformation.OSArchitecture switch
                {
                    Architecture.X64 => "osx-x64",
                    Architecture.Arm64 => "osx-arm64",
                    _ => "unsupported"
                };
            }

            return "unsupported";
        }
        catch
        {
            return "unknown";
        }
    }

    private static DateTimeOffset DefaultNowProvider() => DateTimeOffset.UtcNow;

    private static string DefaultDiagnosticsExportRootProvider() => Path.GetFullPath(Path.Combine("artifacts", "diagnostics"));
}
