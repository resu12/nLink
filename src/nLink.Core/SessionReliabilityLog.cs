using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NLink.Core;

public static class SessionReliabilityLog
{
    private const int MaxEntries = 50;
    private static readonly object Gate = new();
    private static readonly List<SessionReliabilityRecord> Records = new();
    private static string? storagePathOverride;

    public static SessionReliabilityAttempt StartAttempt(string mode, string transport)
    {
        var attempt = new SessionReliabilityAttempt(
            NormalizeMode(mode),
            NormalizeTransport(transport),
            DateTimeOffset.UtcNow);

        RecordStage(attempt, SessionReliabilityStage.Started);
        return attempt;
    }

    public static void RecordStage(
        SessionReliabilityAttempt attempt,
        SessionReliabilityStage stage,
        string? errorCode = null,
        string? errorHint = null)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        AddRecord(new SessionReliabilityRecord(
            TimestampUtc: DateTimeOffset.UtcNow,
            Mode: attempt.Mode,
            Transport: attempt.Transport,
            Stage: stage.ToString(),
            DurationMs: Math.Max(0, (long)(DateTimeOffset.UtcNow - attempt.StartedUtc).TotalMilliseconds),
            ErrorCode: SanitizeShort(errorCode),
            ErrorHint: SanitizeHint(errorHint)));
    }

    public static void RecordStandalone(
        string mode,
        string transport,
        SessionReliabilityStage stage,
        string? errorCode = null,
        string? errorHint = null)
    {
        AddRecord(new SessionReliabilityRecord(
            TimestampUtc: DateTimeOffset.UtcNow,
            Mode: NormalizeMode(mode),
            Transport: NormalizeTransport(transport),
            Stage: stage.ToString(),
            DurationMs: 0,
            ErrorCode: SanitizeShort(errorCode),
            ErrorHint: SanitizeHint(errorHint)));
    }

    public static IReadOnlyList<SessionReliabilityRecord> SnapshotRecent(int max = 50)
    {
        lock (Gate)
        {
            var take = Math.Clamp(max, 1, MaxEntries);
            return Records
                .TakeLast(take)
                .ToArray();
        }
    }

    public static string FormatRecentAsText(int max = 20)
    {
        var rows = SnapshotRecent(max);
        if (rows.Count == 0)
        {
            return "No recent reliability entries.";
        }

        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            var result = GetResult(row);
            sb.Append(row.TimestampUtc.ToString("u", CultureInfo.InvariantCulture))
              .Append(" | ")
              .Append(row.Mode)
              .Append(" | ")
              .Append(row.Transport)
              .Append(" | ")
              .Append(result)
              .Append(" | ")
              .Append(row.Stage);

            if (!string.IsNullOrWhiteSpace(row.ErrorCode))
            {
                sb.Append(" | ").Append(row.ErrorCode);
            }

            if (!string.IsNullOrWhiteSpace(row.ErrorHint))
            {
                sb.Append(" | ").Append(row.ErrorHint);
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    public static void ResetForTests()
    {
        lock (Gate)
        {
            Records.Clear();
        }
    }

    public static void SetStoragePathOverrideForTests(string? path)
    {
        lock (Gate)
        {
            storagePathOverride = path;
        }
    }

    internal static string SerializeForTests(SessionReliabilityRecord record) =>
        JsonSerializer.Serialize(ToPersisted(record));

    private static void AddRecord(SessionReliabilityRecord record)
    {
        lock (Gate)
        {
            Records.Add(record);
            if (Records.Count > MaxEntries)
            {
                Records.RemoveRange(0, Records.Count - MaxEntries);
            }
        }

        TryAppendJsonLine(record);
    }

    private static void TryAppendJsonLine(SessionReliabilityRecord record)
    {
        try
        {
            var path = ResolveStoragePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var line = JsonSerializer.Serialize(ToPersisted(record)) + Environment.NewLine;
            File.AppendAllText(path, line, Encoding.UTF8);
        }
        catch
        {
            // Best-effort local logging only.
        }
    }

    private static PersistedSessionReliabilityRecord ToPersisted(SessionReliabilityRecord record) => new()
    {
        TimestampUtc = record.TimestampUtc,
        Mode = record.Mode,
        Transport = record.Transport,
        Stage = record.Stage,
        DurationMs = record.DurationMs,
        ErrorCode = record.ErrorCode,
        ErrorHint = record.ErrorHint,
    };

    private static string ResolveStoragePath()
    {
        lock (Gate)
        {
            if (!string.IsNullOrWhiteSpace(storagePathOverride))
            {
                return storagePathOverride!;
            }
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "nLink", "reliability.jsonl");
    }

    private static string NormalizeMode(string mode) =>
        string.Equals(mode, "Helpee", StringComparison.OrdinalIgnoreCase) ? "Helpee" : "Helper";

    private static string NormalizeTransport(string transport) =>
        string.Equals(transport, "NKN", StringComparison.OrdinalIgnoreCase) ? "NKN" : "DevLocal";

    private static string? SanitizeShort(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > 64)
        {
            trimmed = trimmed[..64];
        }

        return RedactLongSecretLikeTokens(trimmed);
    }

    private static string? SanitizeHint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (normalized.Length > 180)
        {
            normalized = normalized[..180];
        }

        normalized = RedactLongSecretLikeTokens(normalized);
        return normalized.Length == 0 ? null : normalized;
    }

    private static string RedactLongSecretLikeTokens(string input)
    {
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (!LooksLikeSecret(parts[i]))
            {
                continue;
            }

            parts[i] = "[redacted]";
        }

        return string.Join(' ', parts);
    }

    private static bool LooksLikeSecret(string token)
    {
        if (token.Length < 24)
        {
            return false;
        }

        var candidate = token.Trim(',', ';', ':', '.', '"', '\'', '[', ']', '(', ')');
        if (candidate.Length < 24)
        {
            return false;
        }

        var base64ish = 0;
        foreach (var ch in candidate)
        {
            if (char.IsLetterOrDigit(ch) || ch is '+' or '/' or '=' or '-' or '_')
            {
                base64ish++;
                continue;
            }

            return false;
        }

        return base64ish >= 24;
    }

    private static string GetResult(SessionReliabilityRecord row)
    {
        if (string.Equals(row.Stage, SessionReliabilityStage.Completed.ToString(), StringComparison.Ordinal))
        {
            return "Completed";
        }

        return string.IsNullOrWhiteSpace(row.ErrorCode) ? "InProgress" : "Failed";
    }

    private sealed class PersistedSessionReliabilityRecord
    {
        public DateTimeOffset TimestampUtc { get; set; }
        public string Mode { get; set; } = string.Empty;
        public string Transport { get; set; } = string.Empty;
        public string Stage { get; set; } = string.Empty;
        public long DurationMs { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorHint { get; set; }
    }
}

public sealed class SessionReliabilityAttempt
{
    internal SessionReliabilityAttempt(string mode, string transport, DateTimeOffset startedUtc)
    {
        Mode = mode;
        Transport = transport;
        StartedUtc = startedUtc;
    }

    public string Mode { get; }

    public string Transport { get; }

    internal DateTimeOffset StartedUtc { get; }
}

public readonly record struct SessionReliabilityRecord(
    DateTimeOffset TimestampUtc,
    string Mode,
    string Transport,
    string Stage,
    long DurationMs,
    string? ErrorCode,
    string? ErrorHint);

public enum SessionReliabilityStage
{
    Started,
    CodeGenerated,
    DiscoveryStarted,
    DiscoveryFoundHost,
    DiscoveryTimeout,
    JoinRequestSent,
    IncomingJoinRequest,
    Approved,
    Rejected,
    SessionKeyReady,
    ChatSent,
    ChatReceived,
    Disconnected,
    Completed,
}
