using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using NLink.App.Configuration;
using NLink.App.Threading;
using NLink.Core.Chat;
using NLink.Core;
using NLink.Infra.Nkn;

namespace NLink.App.ViewModels;

public sealed class DiagnosticsPageViewModel : ViewModelBase
{
    private CancellationTokenSource? copyFeedbackCts;
    private string copyFeedbackText = string.Empty;
    private bool showCopyFeedback;

    public DiagnosticsPageViewModel(Action backAction, TransportRuntimeConfig transportConfig)
    {
        BackCommand = new RelayCommand(backAction);

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

    public IRelayCommand BackCommand { get; }

    public event EventHandler<string>? CopyReliabilityLogRequested;

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

    private string BuildDiagnosticsCopyText()
    {
        var lines = new List<string>
        {
            PageTitle,
            PageSubtitle,
            string.Empty,
            $"Connection method: {ActiveTransport}",
            $"Transport: {TransportSummary}",
            $"Method code: {TransportKey}",
            $"Build type: {BuildMode}",
            $"App version: {AppVersion}",
            $"App setting: {EnvironmentValue}",
            $"Auto-selected: {AutoSelected}",
            $"Forced by environment: {ForcedByEnvironment}",
            $"Why this was chosen: {SelectionReason}",
            $"Built-in web page view: {EmbeddedWebViewDefault}",
            string.Empty,
            $"NKN address: {NknAddress}",
            $"Bridge PID: {BridgePid}",
            $"Node/SDK: {NodeSdk}",
            $"Last heartbeat: {LastHeartbeat}",
            $"Bridge restarts: {BridgeRestarts}",
            $"Last bridge exit: {LastBridgeExit}",
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
            $"messages_sent: {MessagesSent}",
            $"messages_received: {MessagesReceived}",
            $"chat_sent: {ChatSent}",
            $"chat_received: {ChatReceived}",
            $"decrypt_failed: {DecryptFailed}",
            $"last_error: {LastError}",
            string.Empty,
            $"{RecentConnectionAttemptsTitle}:",
            RecentConnectionAttemptsText
        };

        return string.Join(Environment.NewLine, lines);
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

    private static string BuildBridgeMessageKind(bool? isTopic)
    {
        return isTopic switch
        {
            true => "topic",
            false => "direct",
            null => "(none)"
        };
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
}
