using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NLink.Core.SessionConnect;

namespace NLink.Core.Logging;

public static partial class LocalOperationalLog
{
    private const int RecentEntryCapacity = 512;
    private const long AcceptanceLogMaximumBytes = 4L * 1024L * 1024L * 1024L;
    private static readonly object Gate = new();
    private static readonly RollingFileLogger Logger = CreateLogger();
    private static readonly OrderedOperationalLogWritePump WritePump = new(Logger, WriteMarkerToStandardError);
    private static readonly Queue<string> RecentEntries = new();
    private static readonly string WriterId = CreateWriterId();
    private static long writerSequence;
    private static long writerBatchGeneration;
    private static long lastAttemptUtcTicks;
    private static bool writerStartAttempted;
    private static bool writerCompletionAttempted;

    public static string LogsDirectoryPath => Logger.LogsDirectoryPath;

    public static string LogFilePath => Logger.LogFilePath;

    public static void Info(string source, string message) => Write("INFO", source, message);

    public static void Warn(string source, string message) => Write("WARN", source, message);

    public static void Error(string source, string message) => Write("ERROR", source, message);

    // One diagnostic decision, three ordinary write attempts. This does not
    // combine persistence receipts or make a failed entry successful.
    internal static void InfoErrorWarn(string source, string info, string error, string warning)
    {
        lock (Gate)
        {
            Info(source, info);
            Error(source, error);
            Warn(source, warning);
        }
    }

    public static void LogAppStart(string? appVersion = null)
    {
        var version = string.IsNullOrWhiteSpace(appVersion) ? ResolveInformationalVersion() : appVersion!;
        Info("App", $"app start | version={version}");
        var inviteSecurity = InviteSecurityDiagnostics.Snapshot();
        Warn(
            "Security",
            $"event=invite_security_status; version={version}; mode={inviteSecurity.Mode}; signing={inviteSecurity.SigningConfiguration}; public_invite_flow={inviteSecurity.PublicInviteFlow}; release_ready={(inviteSecurity.ReleaseReady ? "yes" : "no")}; warning={inviteSecurity.Warning}");
    }

    private static FrontierWriteReceipt? Write(string level, string source, string message,
        FrontierWriterRegistration? registration = null, object? association = null,
        FrontierWriteKind kind = FrontierWriteKind.Completion)
    {
        try
        {
            lock (Gate)
            {
                EnsureWriterStartAttemptedLocked();
                if (writerCompletionAttempted && frontierWriter is { } completedWriter)
                {
                    // A new write attempt is not normal catalog retirement: it
                    // invalidates even an acknowledged/completed app handoff.
                    if (OperatingSystem.IsWindows()) { completedWriter.Handoff?.Cancel(); }
                    RevokeFrontierWriterLocked(completedWriter);
                }
                var safeSource = SensitiveDataRedactor.Redact(source);
                var safeMessage = SensitiveDataRedactor.Redact(message);
                var line = $"[{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss'Z'}] [{level}] [{safeSource}] {safeMessage}";
                var batch = CreatePersistedEntryBatchLocked(line, isDrain: false);
                FrontierWriteReceipt? receipt = null;
                if (registration is not null && association is not null && TryBeginFrontierWriteLocked(registration, association, kind))
                {
                    _ = WritePump.SubmitWithSourceReceipt(batch, out var exact);
                    if (exact is not null && IsFrontierWriterCurrentLocked(registration))
                    {
                        receipt = new(registration, association, exact, batch, kind);
                        (kind == FrontierWriteKind.Marker ? registration.Markers : registration.Writes)[association] = receipt;
                    }
                    else { RevokeFrontierWriterLocked(registration); }
                }
                else { _ = WritePump.Submit(batch); }
                RecentEntries.Enqueue(line);
                while (RecentEntries.Count > RecentEntryCapacity)
                {
                    RecentEntries.Dequeue();
                }
                return receipt;
            }
        }
        catch
        {
            // Logging must never break application flow.
        }
        return null;
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
        => _ = TryWritePersistedEntryLocked(line, isDrain: false);

    private static OperationalLogWriteBatch TryWritePersistedEntryLocked(string line, bool isDrain)
    {
        var batch = CreatePersistedEntryBatchLocked(line, isDrain);
        _ = WritePump.Submit(batch);
        return batch;
    }

    private static OperationalLogWriteBatch CreatePersistedEntryBatchLocked(string line, bool isDrain)
    {
        var physicalLines = line
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var lastSequence = Interlocked.Add(ref writerSequence, physicalLines.Length);
        var firstSequence = lastSequence - physicalLines.Length + 1;
        var batchGeneration = Interlocked.Increment(ref writerBatchGeneration);
        var utcTicks = DateTimeOffset.UtcNow.UtcTicks;
        var attemptUtcTicks = Math.Max(utcTicks, lastAttemptUtcTicks + 1L);
        lastAttemptUtcTicks = attemptUtcTicks;
        var persistedLines = new string[physicalLines.Length];
        for (var index = 0; index < physicalLines.Length; index++)
        {
            persistedLines[index] =
                $"{physicalLines[index]}; log_writer_id={WriterId}; log_writer_sequence={firstSequence + index}" +
                $"; log_writer_batch_generation={batchGeneration}" +
                $"; log_writer_batch_first_sequence={firstSequence}" +
                $"; log_writer_batch_last_sequence={lastSequence}" +
                $"; log_writer_attempt_utc_ticks={attemptUtcTicks}";
        }

        var persistedText = string.Join(Environment.NewLine, persistedLines);
        var canonicalText = string.Join("\n", persistedLines);
        return new OperationalLogWriteBatch(
            GetEnvironmentEvidenceValue("NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID"),
            GetEnvironmentEvidenceValue("NLINK_FILETRANSFER_SCENARIO_RUN_ID"),
            WriterId,
            batchGeneration,
            firstSequence,
            lastSequence,
            attemptUtcTicks,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalText))).ToLowerInvariant(),
            persistedText,
            Encoding.UTF8.GetByteCount(persistedText),
            isDrain);
    }

    private static void WriteMarkerToStandardError(OperationalLogWriteMarker marker)
    {
        try
        {
            Console.Error.WriteLine(FormatWriteMarker(marker));
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

    internal static string FormatWriteMarker(OperationalLogWriteMarker marker)
    {
        var eventName = marker.Kind switch
        {
            OperationalLogWriteMarkerKind.Deferred => "operational_log_write_deferred",
            OperationalLogWriteMarkerKind.Recovered => "operational_log_write_recovered",
            _ => "operational_log_write_failed",
        };
        var batch = marker.Batch;
        return $"event={eventName}" +
               $"; acceptance_run_id={batch.AcceptanceRunId}" +
               $"; scenario_run_id={batch.ScenarioRunId}" +
               $"; log_writer_id={batch.WriterId}" +
               $"; batch_generation={batch.BatchGeneration}" +
               $"; first_sequence={batch.FirstSequence}" +
               $"; last_sequence={batch.LastSequence}" +
               $"; line_count={batch.LastSequence - batch.FirstSequence + 1L}" +
               $"; payload_sha256={batch.PayloadSha256}" +
               $"; attempt_utc_ticks={batch.AttemptUtcTicks}" +
               $"; attempt_count={marker.AttemptCount}" +
               $"; failure_code={marker.FailureCode}";
    }

    internal static string WriterIdForTests => WriterId;

    internal static long WriterSequenceForTests => Volatile.Read(ref writerSequence);

    internal static bool CompleteAndFlush(TimeSpan timeout)
    {
        lock (Gate)
        {
            if (writerCompletionAttempted)
            {
                return WritePump.FlushThroughSequence(writerSequence, timeout);
            }

            writerCompletionAttempted = true;
            EnsureWriterStartAttemptedLocked();
            var lastPayloadSequence = writerSequence;
            var message =
                "event=operational_log_writer_drained" +
                $"; acceptance_run_id={GetEnvironmentEvidenceValue("NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID")}" +
                $"; scenario_run_id={GetEnvironmentEvidenceValue("NLINK_FILETRANSFER_SCENARIO_RUN_ID")}" +
                $"; last_payload_sequence={lastPayloadSequence}";
            var line = $"[{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss'Z'}] [INFO] [OperationalLog] {message}";
            var drainBatch = CreatePersistedEntryBatchLocked(line, isDrain: true);
            if (frontierWriter is { } registration && IsFrontierWriterCurrentLocked(registration))
            {
                OrderedOperationalLogWritePump.ExactBatchReceipt? exact;
                var flushed = registration.Handoff is not null
                    ? WritePump.CompleteAndFlushWithProvisionalSourceReceipt(drainBatch, timeout, out exact)
                    : WritePump.CompleteAndFlushWithSourceReceipt(drainBatch, timeout, out exact);
                if (exact is not null && IsFrontierWriterCurrentLocked(registration))
                {
                    registration.Drain = new(registration, registration, exact, drainBatch, FrontierWriteKind.Drain);
                    registration.DrainFlushed = flushed;
                }
                else { WritePump.ReleaseSource(exact); RevokeFrontierWriterLocked(registration); }
                return flushed;
            }
            return WritePump.CompleteAndFlush(drainBatch, timeout);
        }
    }

    public static string GetRecentLogText()
    {
        lock (Gate)
        {
            return string.Join(Environment.NewLine, RecentEntries);
        }
    }

    internal static bool TryReadRetainedSlice(string startToken, string endToken, out string slice)
    {
        long targetSequence;
        lock (Gate)
        {
            targetSequence = writerSequence;
        }
        if (!WritePump.FlushThroughSequence(targetSequence, TimeSpan.FromSeconds(5)))
        {
            slice = string.Empty;
            return false;
        }

        return Logger.TryReadRetainedSlice(startToken, endToken, out slice);
    }

    internal static bool TryReadRetainedTail(string startToken, out string tail)
    {
        long targetSequence;
        lock (Gate)
        {
            targetSequence = writerSequence;
        }
        if (!WritePump.FlushThroughSequence(targetSequence, TimeSpan.FromSeconds(5)))
        {
            tail = string.Empty;
            return false;
        }

        return Logger.TryReadRetainedTail(startToken, out tail);
    }

    private static string GetDefaultLogPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var logsDir = Path.Combine(localAppData, "nLink", "logs");
        return Path.Combine(logsDir, "nlink.log");
    }

    private static RollingFileLogger CreateLogger()
    {
        var acceptancePath = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_ACCEPTANCE_LOG_PATH");
        var artifactDirectory = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR");
        var acceptanceRunId = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID");
        var scenarioRunId = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_SCENARIO_RUN_ID");
        if (!string.IsNullOrWhiteSpace(acceptancePath) &&
            !string.IsNullOrWhiteSpace(artifactDirectory) &&
            !string.IsNullOrWhiteSpace(acceptanceRunId) &&
            !string.Equals(acceptanceRunId.Trim(), "(none)", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(scenarioRunId) &&
            !string.Equals(scenarioRunId.Trim(), "(none)", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var resolvedArtifactDirectory = Path.GetFullPath(artifactDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var resolvedAcceptancePath = Path.GetFullPath(acceptancePath);
                if (Path.IsPathFullyQualified(acceptancePath) &&
                    resolvedAcceptancePath.StartsWith(resolvedArtifactDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    // The GUI acceptance harness owns this private file and enforces
                    // the same hard bound while it streams evidence. Production logs
                    // retain their normal 2 MiB x 20 rolling policy.
                    return new RollingFileLogger(
                        resolvedAcceptancePath,
                        maxFileBytes: AcceptanceLogMaximumBytes,
                        retainedFileCount: 1);
                }
            }
            catch
            {
                // Invalid acceptance evidence configuration must not redirect
                // ordinary product logs outside the standard local log folder.
            }
        }

        return new RollingFileLogger(GetDefaultLogPath());
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
