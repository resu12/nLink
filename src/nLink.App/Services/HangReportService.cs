using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NLink.Core.Logging;
using NLink.Core.Resources;
using NLink.Infra.Nkn;

namespace NLink.App.Services;

internal enum HangReportTriggerKind
{
    UiWatchdog,
    ManualDiagnostics,
}

internal sealed record HangReportCaptureResult(string FolderPath, string SummaryFilePath);

public sealed class HangReportService
{
    private const int DefaultLogTailLines = 200;
    private readonly SessionRuntime? sessionRuntime;
    private readonly ResourceRuntimeTracker? resourceRuntimeTracker;
    private readonly Func<DateTimeOffset> nowProvider;
    private readonly Func<string> hangArtifactsRootProvider;
    private readonly int logTailLines;
    private readonly object gate = new();
    private DateTimeOffset lastCaptureUtc = DateTimeOffset.MinValue;

    public HangReportService(
        SessionRuntime? sessionRuntime = null,
        ResourceRuntimeTracker? resourceRuntimeTracker = null,
        Func<DateTimeOffset>? nowProvider = null,
        Func<string>? hangArtifactsRootProvider = null,
        int logTailLines = DefaultLogTailLines)
    {
        this.sessionRuntime = sessionRuntime;
        this.resourceRuntimeTracker = resourceRuntimeTracker;
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
        this.hangArtifactsRootProvider = hangArtifactsRootProvider ?? DefaultHangArtifactsRootProvider;
        this.logTailLines = logTailLines > 0 ? logTailLines : DefaultLogTailLines;
    }

    internal HangReportCaptureResult Capture(
        HangReportTriggerKind triggerKind,
        string reason,
        string? diagnosticsTextOverride = null)
    {
        var now = nowProvider();
        var root = hangArtifactsRootProvider();
        Directory.CreateDirectory(root);

        string folderName;
        lock (gate)
        {
            var suffix = now <= lastCaptureUtc ? "-" + Guid.NewGuid().ToString("N")[..8] : string.Empty;
            folderName = $"hang-{now.UtcDateTime:yyyyMMdd-HHmmss}{suffix}";
            lastCaptureUtc = now;
        }

        var folder = Path.Combine(root, folderName);
        Directory.CreateDirectory(folder);

        var diagnosticsSnapshot = sessionRuntime?.GetDiagnosticsSnapshot();
        var activeCounters = ActiveRuntimeCounters.Snapshot();
        var nkn = NknRuntimeDiagnostics.Snapshot();
        var summaryText = BuildSummaryText(now, triggerKind, reason, diagnosticsSnapshot, activeCounters, nkn);

        var summaryPath = Path.Combine(folder, "summary.txt");
        File.WriteAllText(summaryPath, DiagnosticsRedactor.Redact(summaryText));

        var diagnosticsPath = Path.Combine(folder, "diagnostics-snapshot.txt");
        var diagnosticsText = diagnosticsTextOverride ?? BuildDiagnosticsSnapshotText(diagnosticsSnapshot);
        File.WriteAllText(diagnosticsPath, DiagnosticsRedactor.Redact(diagnosticsText));

        var logTailPath = Path.Combine(folder, "log-tail.txt");
        File.WriteAllText(logTailPath, DiagnosticsRedactor.Redact(ReadLogTailText(logTailLines)));

        var resourcePath = Path.Combine(folder, "resource-snapshot.txt");
        File.WriteAllText(resourcePath, DiagnosticsRedactor.Redact(BuildResourceSnapshotText()));

        LocalOperationalLog.Warn(
            "HangReport",
            $"event=captured; trigger={triggerKind}; reason={reason}; path={folder}");

        return new HangReportCaptureResult(folder, summaryPath);
    }

    private static string DefaultHangArtifactsRootProvider()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "nLink", "artifacts", "hang");
    }

    private static string BuildDiagnosticsSnapshotText(DiagnosticsSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return "diagnostics_snapshot: (unavailable)";
        }

        var lines = new[]
        {
            $"privacy_notice: {DiagnosticsExportBuilder.BestEffortPrivacyNotice}",
            string.Empty,
            "diagnostics_snapshot",
            "-------------------",
            $"current_state: {snapshot.Value.CurrentState}",
            $"session_ui_state: {snapshot.Value.SessionUiState}",
            $"attempt: {snapshot.Value.AttemptNumber}",
            $"last_failure_category: {snapshot.Value.LastFailureCategory}",
            $"last_failure_message: {snapshot.Value.LastFailureMessage}",
            $"last_connect_duration_ms: {FormatDuration(snapshot.Value.LastConnectDurationMs)}",
            $"last_handshake_duration_ms: {FormatDuration(snapshot.Value.LastHandshakeDurationMs)}",
            $"last_bridge_start_duration_ms: {FormatDuration(snapshot.Value.LastBridgeStartDurationMs)}",
            $"file_transfer_summary: {snapshot.Value.FileTransferSummary}",
            $"file_transfer_inbound_id: {snapshot.Value.ActiveInboundFileTransferId}",
            $"file_transfer_inbound_state: {snapshot.Value.ActiveInboundFileTransferState}",
            $"file_transfer_inbound_bytes: {snapshot.Value.ActiveInboundFileTransferBytes?.ToString() ?? "(none)"}",
            $"file_transfer_outbound_id: {snapshot.Value.ActiveOutboundFileTransferId}",
            $"file_transfer_outbound_state: {snapshot.Value.ActiveOutboundFileTransferState}",
            $"file_transfer_outbound_bytes: {snapshot.Value.ActiveOutboundFileTransferBytes?.ToString() ?? "(none)"}",
            $"file_transfer_last_failure_code: {snapshot.Value.LastFileTransferFailureCode}",
            $"file_transfer_last_saved_path: {DiagnosticsExportBuilder.RedactStructuredValue("file_transfer_last_saved_path", snapshot.Value.LastFileTransferSavedPath)}",
            $"persistence_summary: {snapshot.Value.PersistenceSummary}",
            $"persistence_warning: {DiagnosticsExportBuilder.RedactStructuredValue("persistence_warning", snapshot.Value.PersistenceWarning)}",
        };
        return string.Join(Environment.NewLine, lines);
    }

    private string BuildResourceSnapshotText()
    {
        if (resourceRuntimeTracker is null)
        {
            return "resource_runtime_tracker: (unavailable)";
        }

        var sb = new StringBuilder();
        var last = resourceRuntimeTracker.GetLastSnapshot();
        var peak = resourceRuntimeTracker.GetPeakSnapshot();

        if (last is null)
        {
            sb.AppendLine("last_snapshot: (none)");
        }
        else
        {
            sb.AppendLine($"last_snapshot_utc: {last.TimestampUtc:u}");
            sb.AppendLine($"app_last_handles: {last.App.HandleCount}");
            sb.AppendLine($"app_last_threads: {last.App.ThreadCount}");
            sb.AppendLine($"app_last_private_bytes_mb: {last.App.PrivateBytesMB:F2}");
            sb.AppendLine($"app_last_working_set_mb: {last.App.WorkingSetMB:F2}");
            if (last.Bridge is not null)
            {
                sb.AppendLine($"bridge_last_handles: {last.Bridge.HandleCount}");
                sb.AppendLine($"bridge_last_threads: {last.Bridge.ThreadCount}");
                sb.AppendLine($"bridge_last_private_bytes_mb: {last.Bridge.PrivateBytesMB:F2}");
                sb.AppendLine($"bridge_last_working_set_mb: {last.Bridge.WorkingSetMB:F2}");
            }
        }

        if (peak is not null)
        {
            sb.AppendLine($"app_peak_private_bytes_mb_since_start: {peak.App.PrivateBytesMB:F2}");
            sb.AppendLine($"app_peak_working_set_mb_since_start: {peak.App.WorkingSetMB:F2}");
            if (peak.Bridge is not null)
            {
                sb.AppendLine($"bridge_peak_private_bytes_mb_since_start: {peak.Bridge.PrivateBytesMB:F2}");
                sb.AppendLine($"bridge_peak_working_set_mb_since_start: {peak.Bridge.WorkingSetMB:F2}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildSummaryText(
        DateTimeOffset now,
        HangReportTriggerKind triggerKind,
        string reason,
        DiagnosticsSnapshot? diagnosticsSnapshot,
        ActiveResourceCountersSnapshot activeCounters,
        NknRuntimeDiagnosticsSnapshot nkn)
    {
        var lines = new List<string>
        {
            "hang_report",
            "===========",
            $"privacy_notice: {DiagnosticsExportBuilder.BestEffortPrivacyNotice}",
            $"captured_utc: {now.UtcDateTime:u}",
            $"trigger: {triggerKind}",
            $"reason: {reason}",
            string.Empty,
            "diagnostics_snapshot",
            "-------------------",
            $"current_state: {diagnosticsSnapshot?.CurrentState ?? "(unavailable)"}",
            $"session_ui_state: {diagnosticsSnapshot?.SessionUiState ?? "(unavailable)"}",
            $"attempt: {diagnosticsSnapshot?.AttemptNumber.ToString() ?? "(unavailable)"}",
            $"last_failure_category: {diagnosticsSnapshot?.LastFailureCategory ?? "(unavailable)"}",
            $"last_failure_message: {DiagnosticsExportBuilder.RedactStructuredValue("last_failure_message", diagnosticsSnapshot?.LastFailureMessage ?? "(unavailable)")}",
            $"persistence_summary: {diagnosticsSnapshot?.PersistenceSummary ?? "(unavailable)"}",
            $"persistence_warning: {DiagnosticsExportBuilder.RedactStructuredValue("persistence_warning", diagnosticsSnapshot?.PersistenceWarning ?? "(unavailable)")}",
            string.Empty,
            "active_counters",
            "---------------",
            $"active_sessions: {activeCounters.ActiveSessions}",
            $"active_connect_attempts: {activeCounters.ActiveConnectAttempts}",
            $"active_retry_timers: {activeCounters.ActiveRetryTimers}",
            $"active_watchdogs: {activeCounters.ActiveWatchdogs}",
            $"active_transport_tasks: {activeCounters.ActiveTransportTasks}",
            $"active_bridge_io_readers: {activeCounters.ActiveBridgeIoReaders}",
            string.Empty,
            "bridge",
            "------",
            $"bridge_pid: {(nkn.BridgePid > 0 ? nkn.BridgePid.ToString() : "(not running)")}",
            $"bridge_last_exit_code: {(nkn.BridgeLastExitCode >= 0 ? nkn.BridgeLastExitCode.ToString() : "(none)")}",
            $"bridge_last_exit_reason: {nkn.BridgeLastExitReason}",
            $"bridge_last_pong_utc_ticks: {nkn.BridgeLastPongUtcTicks}",
            $"bridge_restart_count: {nkn.BridgeRestartCount}",
            $"nkn_last_error: {nkn.LastError}",
        };

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatDuration(double? value) =>
        value.HasValue ? value.Value.ToString("F2") : "(none)";

    private static string ReadLogTailText(int tailLines)
    {
        try
        {
            if (!File.Exists(LocalOperationalLog.LogFilePath))
            {
                return "log_tail: (log file not found)";
            }

            var lines = File.ReadLines(LocalOperationalLog.LogFilePath).TakeLast(tailLines);
            return string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            return "log_tail_read_failed: " + ex.GetType().Name;
        }
    }
}
