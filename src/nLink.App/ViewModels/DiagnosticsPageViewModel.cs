using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using NLink.App.Configuration;
using NLink.App.Threading;
using NLink.App.Services;
using NLink.Core.Chat;
using NLink.Core;
using NLink.Core.Logging;
using NLink.Core.Metrics;
using NLink.Infra.Nkn;

namespace NLink.App.ViewModels;

public sealed class DiagnosticsPageViewModel : ViewModelBase
{
    private CancellationTokenSource? copyFeedbackCts;
    private string copyFeedbackText = string.Empty;
    private bool showCopyFeedback;
    private readonly string bugReportUrl;
    private readonly DiagnosticsSnapshot runtimeDiagnosticsSnapshot;
    private readonly MetricsRegistry? metricsRegistry;
    private readonly Func<DateTimeOffset> nowProvider;
    private readonly Func<string> diagnosticsExportRootProvider;

    public DiagnosticsPageViewModel(
        Action backAction,
        TransportRuntimeConfig transportConfig,
        ShareMessageConfig? linksConfig = null,
        SessionRuntime? sessionRuntime = null,
        MetricsRegistry? metricsRegistry = null,
        Func<DateTimeOffset>? nowProvider = null,
        Func<string>? diagnosticsExportRootProvider = null)
    {
        linksConfig ??= new ShareMessageConfig(null);
        BackCommand = new RelayCommand(backAction);
        bugReportUrl = linksConfig.BugReportUrl;
        this.metricsRegistry = metricsRegistry;
        this.nowProvider = nowProvider ?? DefaultNowProvider;
        this.diagnosticsExportRootProvider = diagnosticsExportRootProvider ?? DefaultDiagnosticsExportRootProvider;

        ActiveTransport = transportConfig.DisplayName;
        TransportKey = transportConfig.Key;
        TransportSummary = transportConfig.Key;
        BuildMode = transportConfig.BuildMode;
        EnvironmentValue = transportConfig.EnvironmentVariableValue;
        SelectionReason = transportConfig.SelectionReason;
        AutoSelected = transportConfig.AutoSelected ? "Yes" : "No";
        ForcedByEnvironment = transportConfig.ForcedByEnvironment ? "Yes" : "No";
        EmbeddedWebViewDefault = AppFeatureFlags.UseEmbeddedWebView ? "Enabled by default" : "Disabled by default";
        AppVersion = ResolveAppVersion();
        OsDescription = RuntimeInformation.OSDescription;
        ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString();
        OsArchitecture = RuntimeInformation.OSArchitecture.ToString();
        BridgeResolutionRid = ResolveBridgeRidForDiagnostics();
        runtimeDiagnosticsSnapshot = sessionRuntime?.GetDiagnosticsSnapshot() ?? new DiagnosticsSnapshot(
            CurrentState: "(unknown)",
            SessionUiState: "(unknown)",
            AttemptNumber: 0,
            LastFailureCategory: "(none)",
            LastFailureMessage: "(none)",
            LastConnectDurationMs: null,
            LastHandshakeDurationMs: null,
            LastBridgeStartDurationMs: null);

        if (string.Equals(transportConfig.Key, "NKN", StringComparison.OrdinalIgnoreCase))
        {
            NknRuntimeDiagnostics.EnsureInitialized();
        }

        var counters = ChatRuntimeCounters.Snapshot();
        var nknDiagnostics = NknRuntimeDiagnostics.Snapshot();
        NknAddress = nknDiagnostics.Address;
        MessagesSent = nknDiagnostics.MessagesSent.ToString();
        MessagesReceived = nknDiagnostics.MessagesReceived.ToString();
        LastError = nknDiagnostics.LastError;
        BridgePid = nknDiagnostics.BridgePid > 0 ? nknDiagnostics.BridgePid.ToString() : "(not running)";
        NodeSdk = string.IsNullOrWhiteSpace(nknDiagnostics.NodeVersion) ? "(unknown)" : nknDiagnostics.NodeVersion;
        LastHeartbeat = nknDiagnostics.BridgeLastPongUtcTicks > 0
            ? new DateTimeOffset(nknDiagnostics.BridgeLastPongUtcTicks, TimeSpan.Zero).ToString("u")
            : "(none)";
        BridgeRestarts = nknDiagnostics.BridgeRestartCount.ToString();
        LastBridgeExit = BuildLastBridgeExitText(nknDiagnostics.BridgeLastExitCode, nknDiagnostics.BridgeLastExitReason);
        BridgeRawMessagesReceived = nknDiagnostics.BridgeRawMessagesReceived.ToString();
        LastBridgeMessageSource = nknDiagnostics.LastBridgeMessageSource;
        LastBridgeMessageKind = BuildBridgeMessageKind(nknDiagnostics.LastBridgeMessageIsTopic);
        LastEnvelopeType = nknDiagnostics.LastEnvelopeType;
        LastEnvelopeDropReason = nknDiagnostics.LastEnvelopeDropReason;
        JoinRequestsReceived = nknDiagnostics.JoinRequestsReceived.ToString();
        IncomingJoinRequestRaisedCount = nknDiagnostics.IncomingJoinRequestRaisedCount.ToString();
        AcksReceived = nknDiagnostics.AcksReceived.ToString();
        AcksIgnoredSourceMismatch = nknDiagnostics.AcksIgnoredSourceMismatch.ToString();
        LastDisconnectReason = nknDiagnostics.LastDisconnectReason;
        ChatSent = counters.ChatSent.ToString();
        ChatReceived = counters.ChatReceived.ToString();
        DecryptFailed = counters.ChatDecryptFailed.ToString();
        RecentConnectionAttemptsText = BuildRecentConnectionAttemptsText(SessionReliabilityLog.SnapshotRecent(10));
        CopyReliabilityLogCommand = new RelayCommand(RequestCopyReliabilityLog);
        ExportMetricsJsonCommand = new RelayCommand(ExportMetricsJson);
        OpenLogsFolderCommand = new RelayCommand(RequestOpenLogsFolder);
        ReportBugCommand = new RelayCommand(RequestOpenBugReport);
    }

    public string PageTitle => "App info";

    public string PageSubtitle => "Current app settings and connection method.";

    public string ActiveTransport { get; }

    public string TransportKey { get; }

    public string TransportSummary { get; }

    public string BuildMode { get; }

    public string EnvironmentValue { get; }

    public string SelectionReason { get; }

    public string AutoSelected { get; }

    public string ForcedByEnvironment { get; }

    public string EmbeddedWebViewDefault { get; }

    public string AppVersion { get; }

    public string OsDescription { get; }

    public string ProcessArchitecture { get; }

    public string OsArchitecture { get; }

    public string BridgeResolutionRid { get; }
    public string CurrentTransportState => runtimeDiagnosticsSnapshot.CurrentState;
    public string LastFailureCategory => runtimeDiagnosticsSnapshot.LastFailureCategory;
    public string LastFailureMessage => runtimeDiagnosticsSnapshot.LastFailureMessage;
    public string AttemptNumber => runtimeDiagnosticsSnapshot.AttemptNumber.ToString();
    public string LastConnectDurationMs => FormatDuration(runtimeDiagnosticsSnapshot.LastConnectDurationMs);
    public string LastHandshakeDurationMs => FormatDuration(runtimeDiagnosticsSnapshot.LastHandshakeDurationMs);
    public string LastBridgeStartDurationMs => FormatDuration(runtimeDiagnosticsSnapshot.LastBridgeStartDurationMs);

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

    public string BridgeRawMessagesReceived { get; }

    public string LastBridgeMessageSource { get; }

    public string LastBridgeMessageKind { get; }

    public string LastEnvelopeType { get; }

    public string LastEnvelopeDropReason { get; }

    public string JoinRequestsReceived { get; }

    public string IncomingJoinRequestRaisedCount { get; }

    public string AcksReceived { get; }

    public string AcksIgnoredSourceMismatch { get; }

    public string LastDisconnectReason { get; }

    public string RecentConnectionAttemptsTitle => "Recent connection attempts";

    public string RecentConnectionAttemptsText { get; }

    public bool ShowCopyFeedback
    {
        get => showCopyFeedback;
        private set => SetProperty(ref showCopyFeedback, value);
    }

    public string CopyFeedbackText
    {
        get => copyFeedbackText;
        private set => SetProperty(ref copyFeedbackText, value);
    }

    public IRelayCommand CopyReliabilityLogCommand { get; }
    public IRelayCommand ExportMetricsJsonCommand { get; }

    public IRelayCommand OpenLogsFolderCommand { get; }

    public IRelayCommand ReportBugCommand { get; }

    public IRelayCommand BackCommand { get; }

    public event EventHandler<string>? CopyReliabilityLogRequested;

    public event EventHandler<string>? OpenLogsFolderRequested;

    public event EventHandler<string>? OpenBugReportRequested;
    public event EventHandler<string>? OpenMetricsExportFolderRequested;

    public void NotifyCopySucceeded()
    {
        _ = ShowCopyFeedbackAsync("Copied", success: true);
    }

    public void NotifyCopyFailed()
    {
        _ = ShowCopyFeedbackAsync("Could not copy", success: false);
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
        OpenBugReportRequested?.Invoke(this, bugReportUrl);
    }

    private void ExportMetricsJson()
    {
        try
        {
            var outputPath = ExportMetricsJsonToFile();
            OpenMetricsExportFolderRequested?.Invoke(this, Path.GetDirectoryName(outputPath) ?? diagnosticsExportRootProvider());
            _ = ShowCopyFeedbackAsync("Metrics exported", success: true);
        }
        catch
        {
            _ = ShowCopyFeedbackAsync("Could not export metrics", success: false);
        }
    }

    private async Task ShowCopyFeedbackAsync(string text, bool success)
    {
        copyFeedbackCts?.Cancel();
        copyFeedbackCts?.Dispose();
        copyFeedbackCts = new CancellationTokenSource();
        var ct = copyFeedbackCts.Token;

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
        });
    }

    internal string BuildDiagnosticsCopyTextForTests() => BuildDiagnosticsCopyText();
    internal string ExportMetricsJsonForTests() => ExportMetricsJsonToFile();

    private string BuildDiagnosticsCopyText()
    {
        var metricsSnapshot = metricsRegistry?.Snapshot();
        var timelineText = BuildSessionTimelineText(SessionTimeline.SnapshotRecent(30));
        var lines = new List<string>
        {
            "Status",
            "------",
            $"App version: {AppVersion}",
            $"OS: {OsDescription}",
            $"Process architecture: {ProcessArchitecture}",
            $"OS architecture: {OsArchitecture}",
            $"Bridge RID: {BridgeResolutionRid}",
            $"current_state: {CurrentTransportState}",
            $"attempt: {AttemptNumber}",
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
            string.Empty,
            "Bridge / NKN",
            "------------",
            $"NKN address: {NknAddress}",
            $"Bridge PID: {BridgePid}",
            $"Node/SDK: {NodeSdk}",
            $"Last heartbeat: {LastHeartbeat}",
            $"Bridge restarts: {BridgeRestarts}",
            $"Last bridge exit: {LastBridgeExit}",
            $"bridge_process_status: {BuildBridgeProcessStatus()}",
            $"bridge_raw_messages_received: {BridgeRawMessagesReceived}",
            $"last_bridge_message_kind: {LastBridgeMessageKind}",
            $"last_bridge_message_source: {LastBridgeMessageSource}",
            $"last_envelope_type: {LastEnvelopeType}",
            $"last_envelope_drop_reason: {LastEnvelopeDropReason}",
            $"join_requests_received: {JoinRequestsReceived}",
            $"incoming_join_request_raised: {IncomingJoinRequestRaisedCount}",
            $"acks_received: {AcksReceived}",
            $"acks_ignored_source_mismatch: {AcksIgnoredSourceMismatch}",
            $"last_disconnect_reason: {LastDisconnectReason}",
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
            "Errors",
            "------",
            $"last_failure_category: {LastFailureCategory}",
            $"last_failure_message: {LastFailureMessage}",
            $"last_error: {LastError}",
            string.Empty,
            "Session timeline (last 30)",
            "----------------------",
            timelineText,
            string.Empty,
            $"{RecentConnectionAttemptsTitle}:",
            RecentConnectionAttemptsText
        };

        return SensitiveDataRedactor.Redact(string.Join(Environment.NewLine, lines));
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

        return string.Join(Environment.NewLine, lines);
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
