using System.Reflection;
using NLink.Core.SessionConnect;

namespace NLink.Core.Logging;

public static class LocalOperationalLog
{
    private const int RecentEntryCapacity = 512;
    private static readonly object Gate = new();
    private static readonly RollingFileLogger Logger = new(GetDefaultLogPath());
    private static readonly Queue<string> RecentEntries = new();
    private static readonly string WriterId = CreateWriterId();
    private static long writerSequence;
    private static bool writerStartAttempted;

    public static string LogsDirectoryPath => Logger.LogsDirectoryPath;

    public static string LogFilePath => Logger.LogFilePath;

    public static void Info(string source, string message) => Write("INFO", source, message);

    public static void Warn(string source, string message) => Write("WARN", source, message);

    public static void Error(string source, string message) => Write("ERROR", source, message);

    public static void LogAppStart(string? appVersion = null)
    {
        var version = string.IsNullOrWhiteSpace(appVersion) ? ResolveInformationalVersion() : appVersion!;
        Info("App", $"app start | version={version}");
        var inviteSecurity = InviteSecurityDiagnostics.Snapshot();
        Warn(
            "Security",
            $"event=invite_security_status; version={version}; mode={inviteSecurity.Mode}; signing={inviteSecurity.SigningConfiguration}; public_invite_flow={inviteSecurity.PublicInviteFlow}; release_ready={(inviteSecurity.ReleaseReady ? "yes" : "no")}; warning={inviteSecurity.Warning}");
    }

    private static void Write(string level, string source, string message)
    {
        try
        {
            lock (Gate)
            {
                EnsureWriterStartAttemptedLocked();
                var safeSource = SensitiveDataRedactor.Redact(source);
                var safeMessage = SensitiveDataRedactor.Redact(message);
                var line = $"[{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss'Z'}] [{level}] [{safeSource}] {safeMessage}";
                TryWritePersistedEntryLocked(line);
                RecentEntries.Enqueue(line);
                while (RecentEntries.Count > RecentEntryCapacity)
                {
                    RecentEntries.Dequeue();
                }
            }
        }
        catch
        {
            // Logging must never break application flow.
        }
    }

    private static void EnsureWriterStartAttemptedLocked()
    {
        if (writerStartAttempted)
        {
            return;
        }

        writerStartAttempted = true;
        var acceptanceRunId = GetEnvironmentEvidenceValue("NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID");
        var scenarioRunId = GetEnvironmentEvidenceValue("NLINK_FILETRANSFER_SCENARIO_RUN_ID");
        var message =
            "event=operational_log_writer_started" +
            $"; acceptance_run_id={acceptanceRunId}" +
            $"; scenario_run_id={scenarioRunId}";
        var line = $"[{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss'Z'}] [INFO] [OperationalLog] {message}";
        TryWritePersistedEntryLocked(line);
    }

    private static void TryWritePersistedEntryLocked(string line)
    {
        var physicalLines = line
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var lastSequence = Interlocked.Add(ref writerSequence, physicalLines.Length);
        var firstSequence = lastSequence - physicalLines.Length + 1;
        var persistedLines = new string[physicalLines.Length];
        for (var index = 0; index < physicalLines.Length; index++)
        {
            persistedLines[index] =
                $"{physicalLines[index]}; log_writer_id={WriterId}; log_writer_sequence={firstSequence + index}";
        }

        var result = Logger.TryWriteLine(string.Join(Environment.NewLine, persistedLines));
        if (result.Success)
        {
            return;
        }

        for (var sequence = firstSequence; sequence <= lastSequence; sequence++)
        {
            TryWriteFailureMarkerToStandardError(sequence, result.FailureCode);
        }
    }

    private static void TryWriteFailureMarkerToStandardError(long sequence, string failureCode)
    {
        try
        {
            Console.Error.WriteLine(FormatWriteFailureMarker(WriterId, sequence, failureCode));
        }
        catch
        {
            // Producer-side evidence remains best effort and must never affect runtime behavior.
        }
    }

    internal static string FormatWriteFailureMarker(string writerId, long sequence, string failureCode)
        => "event=operational_log_write_failed" +
           $"; log_writer_id={writerId}" +
           $"; log_writer_sequence={sequence}" +
           $"; failure_code={failureCode}";

    internal static string WriterIdForTests => WriterId;

    internal static long WriterSequenceForTests => Volatile.Read(ref writerSequence);

    public static string GetRecentLogText()
    {
        lock (Gate)
        {
            return string.Join(Environment.NewLine, RecentEntries);
        }
    }

    internal static bool TryReadRetainedSlice(string startToken, string endToken, out string slice)
    {
        lock (Gate)
        {
            return Logger.TryReadRetainedSlice(startToken, endToken, out slice);
        }
    }

    internal static bool TryReadRetainedTail(string startToken, out string tail)
    {
        lock (Gate)
        {
            return Logger.TryReadRetainedTail(startToken, out tail);
        }
    }

    private static string GetDefaultLogPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var logsDir = Path.Combine(localAppData, "nLink", "logs");
        return Path.Combine(logsDir, "nlink.log");
    }

    private static string CreateWriterId()
    {
        var nonce = Guid.NewGuid().ToString("N")[..16];
        return $"p{Environment.ProcessId}r{nonce}";
    }

    private static string GetEnvironmentEvidenceValue(string name)
    {
        try
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? "(none)" : value.Trim();
        }
        catch
        {
            return "(none)";
        }
    }

    private static string ResolveInformationalVersion()
    {
        try
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(LocalOperationalLog).Assembly;
            var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
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
