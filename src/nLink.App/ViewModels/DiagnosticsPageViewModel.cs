using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using NLink.App.Configuration;
using NLink.App.Services;
using NLink.App.Services.ScreenCapture;
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

    private readonly InlineTransientText copyFeedback = new();
    private readonly string? bugReportUrl;
    private readonly DiagnosticsSnapshot runtimeDiagnosticsSnapshot;
    private readonly MetricsRegistry? metricsRegistry;
    private readonly ResourceRuntimeTracker? resourceRuntimeTracker;
    private readonly HangReportService? hangReportService;
    private readonly Func<DateTimeOffset> nowProvider;
    private readonly Func<string> diagnosticsExportRootProvider;
    private readonly Action<string, string, string, string> persistScreenSharePresetInBackground;
    private readonly InviteSecurityStatus inviteSecurityStatus;
    private readonly NknRuntimeDiagnosticsSnapshot nknDiagnosticsSnapshot;
    private readonly PersistenceDiagnosticsSnapshot persistenceDiagnosticsSnapshot;
    private readonly ScreenShareEvidenceSnapshot screenShareEvidenceSnapshot;
    private readonly ScreenShareLiveDiagnosticsSnapshot screenShareLiveSnapshot;

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
        Action<string, string, string, string>? screenSharePresetPersistence = null)
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
        Action<string, string, string, string>? screenSharePresetPersistence = null)
    {
        linksConfig ??= new ShareMessageConfig(null);
        BackCommand = new RelayCommand(backAction);
        bugReportUrl = linksConfig.BugReportUrl;
        this.metricsRegistry = metricsRegistry;
        this.resourceRuntimeTracker = resourceRuntimeTracker;
        this.hangReportService = hangReportService;
        this.nowProvider = nowProvider ?? DefaultNowProvider;
        this.diagnosticsExportRootProvider = diagnosticsExportRootProvider ?? DefaultDiagnosticsExportRootProvider;
        persistScreenSharePresetInBackground = screenSharePresetPersistence ?? PersistScreenSharePresetInBackground;
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
        ApplyHighPerformanceScreenSharePresetCommand = new RelayCommand(ApplyHighPerformanceScreenSharePreset);
    }

    public string PageTitle => "Diagnostics";

    public string PageSubtitle => "Support status, screen share health, and capture tools.";

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
    public string ScreenShareEffectivePresetName => ScreenShareQualitySettings.GetCurrentEnvironmentState().EffectivePresetName;
    public string ScreenSharePresetMigrationStatus => ScreenShareQualitySettings.WasLegacyHigherClarityPresetMigrated ? "Yes" : "No";
    public string ScreenSharePresetBalanced => ScreenShareQualitySettings.BalancedPreset.Describe();
    public string ScreenSharePresetHighQuality => ScreenShareQualitySettings.HighQualityPreset.Describe();
    public string ScreenSharePresetHighPerformance => ScreenShareQualitySettings.HighPerformancePreset.Describe();
    public string ScreenShareCaptureEnvHint => "Apply preset, then restart screen sharing. Settings apply instantly and are persisted in background via env vars: NLINK_FEATURE_SCREENCAP_MAX_FPS, NLINK_FEATURE_SCREENCAP_TRANSPORT_MAX_FPS, NLINK_FEATURE_SCREENCAP_SCALE.";
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
    public IRelayCommand ApplyHighPerformanceScreenSharePresetCommand { get; }

    public IRelayCommand BackCommand { get; }

    public event EventHandler<string>? CopyReliabilityLogRequested;

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

    private void RequestCopyReliabilityLog()
    {
        var text = BuildDiagnosticsCopyText();
        CopyReliabilityLogRequested?.Invoke(this, text);
    }

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

    private void ApplyHighPerformanceScreenSharePreset()
        => ApplyScreenSharePreset(ScreenShareQualitySettings.HighPerformancePreset);

    private void ApplyScreenSharePreset(ScreenSharePresetDefinition preset)
    {
        var fpsText = preset.CaptureFramesPerSecond.ToString(CultureInfo.InvariantCulture);
        var transportFpsText = preset.TransportFramesPerSecond.ToString(CultureInfo.InvariantCulture);
        var scaleText = preset.CaptureScale.ToString("0.##", CultureInfo.InvariantCulture);

        Environment.SetEnvironmentVariable(ScreenShareMaxFpsVariable, fpsText);
        Environment.SetEnvironmentVariable(ScreenShareTransportMaxFpsVariable, transportFpsText);
        Environment.SetEnvironmentVariable(ScreenShareScaleVariable, scaleText);

        OnPropertyChanged(nameof(ScreenShareCaptureMaxFps));
        OnPropertyChanged(nameof(ScreenShareTransportMaxFps));
        OnPropertyChanged(nameof(ScreenShareCaptureScale));
        OnPropertyChanged(nameof(ScreenShareEffectivePresetName));
        OnPropertyChanged(nameof(ScreenSharePresetMigrationStatus));
        OnPropertyChanged(nameof(AdvancedScreenShareSettingsSummary));
        OnPropertyChanged(nameof(ShowScreenShareResetHint));

        copyFeedback.Show($"{preset.DisplayName} preset applied");
        persistScreenSharePresetInBackground(preset.DisplayName, fpsText, transportFpsText, scaleText);
    }

    private static void PersistScreenSharePresetInBackground(
        string presetName,
        string fpsText,
        string transportFpsText,
        string scaleText)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var persisted = TrySetUserEnvironmentVariable(ScreenShareMaxFpsVariable, fpsText) &&
                                TrySetUserEnvironmentVariable(ScreenShareTransportMaxFpsVariable, transportFpsText) &&
                                TrySetUserEnvironmentVariable(ScreenShareScaleVariable, scaleText);
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
        copyFeedback.Dispose();
    }

    internal string BuildDiagnosticsCopyTextForTests() => BuildDiagnosticsCopyText();
    internal string BuildScreenShareEvidenceTextForTests() => BuildScreenShareEvidenceText();
    internal string ExportMetricsJsonForTests() => ExportMetricsJsonToFile();

    private string BuildScreenShareEvidenceText()
        => DiagnosticsRedactor.Redact(screenShareEvidenceSnapshot.ToReportText());

    private string BuildDiagnosticsCopyText()
    {
        var metricsSnapshot = metricsRegistry?.Snapshot();
        var timelineText = BuildSessionTimelineText(SessionTimeline.SnapshotRecent(30));
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
            $"screenshare_capture_max_fps: {FeatureFlags.ScreenShareMaxFps}",
            $"screenshare_transport_max_fps: {FeatureFlags.ScreenShareTransportMaxFps}",
            $"screenshare_transport_autotune: {FormatFeatureFlag(FeatureFlags.ScreenShareTransportAutoTuneEnabled)}",
            $"screenshare_capture_scale: {FeatureFlags.ScreenShareScale:0.###}",
            $"screenshare_effective_preset: {ScreenShareEffectivePresetName}",
            $"screenshare_legacy_preset_migrated: {ScreenSharePresetMigrationStatus}",
            string.Empty,
            "screenshare_capture_presets:",
            $"  balanced_default: {ScreenSharePresetBalanced}",
            $"  high_quality: {ScreenSharePresetHighQuality}",
            $"  high_performance: {ScreenSharePresetHighPerformance}",
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
        => $"preset={ScreenShareEffectivePresetName}; capture_fps={ScreenShareCaptureMaxFps}; transport_fps={ScreenShareTransportMaxFps}; scale={ScreenShareCaptureScale}; legacy_migrated={ScreenSharePresetMigrationStatus}";

    private static string FormatOptionalDouble(double value)
        => value >= 0 ? value.ToString("F2", CultureInfo.InvariantCulture) : "(none)";

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
