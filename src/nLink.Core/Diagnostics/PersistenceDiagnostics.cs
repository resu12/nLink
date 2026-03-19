using System;
using System.Collections.Generic;
using System.Linq;
using NLink.Core.Logging;

namespace NLink.Core.Diagnostics;

public enum PersistenceDiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public enum PersistenceDiagnosticOutcome
{
    None,
    Fallback,
    Partial,
    FailedClosed,
}

public readonly record struct PersistenceDiagnosticEvent(
    DateTimeOffset TimestampUtc,
    string Domain,
    string Operation,
    PersistenceDiagnosticSeverity Severity,
    PersistenceDiagnosticOutcome Outcome,
    string Reason,
    string UserWarning);

public readonly record struct PersistenceDiagnosticsSnapshot(
    string Summary,
    string LastWarning,
    int WarningCount,
    int ErrorCount,
    IReadOnlyList<PersistenceDiagnosticEvent> RecentEvents);

public static class PersistenceDiagnostics
{
    private const int MaxEvents = 24;
    private static readonly object Gate = new();
    private static readonly List<PersistenceDiagnosticEvent> Events = new(MaxEvents);

    public static void Record(
        string domain,
        string operation,
        PersistenceDiagnosticSeverity severity,
        PersistenceDiagnosticOutcome outcome,
        string? reason,
        string? userWarning = null)
    {
        var normalizedDomain = Normalize(domain, "unknown");
        var normalizedOperation = Normalize(operation, "unknown");
        var normalizedReason = NormalizeReason(reason);
        var normalizedWarning = Normalize(userWarning, "(none)");
        var entry = new PersistenceDiagnosticEvent(
            TimestampUtc: DateTimeOffset.UtcNow,
            Domain: normalizedDomain,
            Operation: normalizedOperation,
            Severity: severity,
            Outcome: outcome,
            Reason: normalizedReason,
            UserWarning: normalizedWarning);

        lock (Gate)
        {
            if (Events.Count == MaxEvents)
            {
                Events.RemoveAt(0);
            }

            Events.Add(entry);
        }

        var logLevel = severity == PersistenceDiagnosticSeverity.Error ? "Warn" : "Info";
        var message = $"event=persistence_diagnostic; domain={normalizedDomain}; operation={normalizedOperation}; severity={severity}; outcome={outcome}; reason={normalizedReason}; warning={normalizedWarning}";
        if (string.Equals(logLevel, "Warn", StringComparison.Ordinal))
        {
            LocalOperationalLog.Warn("Persistence", message);
        }
        else
        {
            LocalOperationalLog.Info("Persistence", message);
        }
    }

    public static PersistenceDiagnosticsSnapshot Snapshot()
    {
        lock (Gate)
        {
            var recent = Events.ToArray();
            var warningCount = recent.Count(e => e.Severity == PersistenceDiagnosticSeverity.Warning);
            var errorCount = recent.Count(e => e.Severity == PersistenceDiagnosticSeverity.Error);
            var last = recent.LastOrDefault();
            var summary = recent.Length == 0
                ? "Healthy"
                : $"{last.Severity}: {last.Domain} {last.Operation} ({last.Outcome}; {last.Reason})";
            var lastWarning = recent.LastOrDefault(e => !string.Equals(e.UserWarning, "(none)", StringComparison.Ordinal)).UserWarning;
            if (string.IsNullOrWhiteSpace(lastWarning))
            {
                lastWarning = "(none)";
            }

            return new PersistenceDiagnosticsSnapshot(
                Summary: summary,
                LastWarning: lastWarning,
                WarningCount: warningCount,
                ErrorCount: errorCount,
                RecentEvents: recent);
        }
    }

    internal static void ClearForTests()
    {
        lock (Gate)
        {
            Events.Clear();
        }
    }

    private static string Normalize(string? value, string fallback)
    {
        var normalized = SensitiveDataRedactor.Redact(value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static string NormalizeReason(string? reason)
    {
        var normalized = Normalize(reason, "(none)");
        if (normalized.Length <= 120)
        {
            return normalized;
        }

        return normalized[..120];
    }
}
