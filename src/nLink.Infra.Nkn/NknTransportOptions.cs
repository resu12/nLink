using System.Diagnostics;
using System.Text.Json;
using NLink.Core.Diagnostics;
using NLink.Core.Logging;

namespace NLink.Infra.Nkn;

internal sealed class NknTransportOptions
{
    private static Func<bool>? shouldUsePerProcessLocalIdentityOverrideForTests;
    private static Func<string>? localAppDataPathOverrideForTests;
    private static Func<int, bool>? isProcessRunningOverrideForTests;
    private static Func<string, string[]>? enumerateInstanceIdentityFilesOverrideForTests;
    private static Action<string>? deleteFileOverrideForTests;

    private NknTransportOptions()
    {
    }

    public string? SeedRpc { get; private set; }

    public string? Identifier { get; private set; }

    public string KeyPath { get; private set; } = string.Empty;

    public bool PreflightRpcEnabled { get; private set; }

    public int PreflightTimeoutMs { get; private set; }

    public int PreflightConcurrency { get; private set; }

    public int PreflightCacheTtlMs { get; private set; }

    public int FileTransferChunkPacingMs { get; private set; }

    public static NknTransportOptions Load()
    {
        var appSettings = AppSettingsJson.Load();

        var seedRpc = FirstNonEmpty(
            Environment.GetEnvironmentVariable("NLINK_NKN_SEED_RPC"),
            appSettings.Get("NLINK_NKN_SEED_RPC"),
            appSettings.Get("nLink:nkn:seedRpc"));

        var identifier = FirstNonEmpty(
            Environment.GetEnvironmentVariable("NLINK_NKN_IDENTIFIER"),
            appSettings.Get("NLINK_NKN_IDENTIFIER"),
            appSettings.Get("nLink:nkn:identifier"));

        var configuredKeyPath = FirstNonEmpty(
            Environment.GetEnvironmentVariable("NLINK_NKN_KEY_PATH"),
            appSettings.Get("NLINK_NKN_KEY_PATH"),
            appSettings.Get("nLink:nkn:keyPath"));

        var preflightRpcEnabled = FirstNonEmpty(
            Environment.GetEnvironmentVariable("NLINK_NKN_PREFLIGHT_RPC_ENABLED"),
            appSettings.Get("NLINK_NKN_PREFLIGHT_RPC_ENABLED"),
            appSettings.Get("nLink:nkn:preflightRpcEnabled"));

        var preflightTimeoutMs = FirstNonEmpty(
            Environment.GetEnvironmentVariable("NLINK_NKN_PREFLIGHT_TIMEOUT_MS"),
            appSettings.Get("NLINK_NKN_PREFLIGHT_TIMEOUT_MS"),
            appSettings.Get("nLink:nkn:preflightTimeoutMs"));

        var preflightConcurrency = FirstNonEmpty(
            Environment.GetEnvironmentVariable("NLINK_NKN_PREFLIGHT_CONCURRENCY"),
            appSettings.Get("NLINK_NKN_PREFLIGHT_CONCURRENCY"),
            appSettings.Get("nLink:nkn:preflightConcurrency"));

        var preflightCacheTtlMs = FirstNonEmpty(
            Environment.GetEnvironmentVariable("NLINK_NKN_PREFLIGHT_CACHE_TTL_MS"),
            appSettings.Get("NLINK_NKN_PREFLIGHT_CACHE_TTL_MS"),
            appSettings.Get("nLink:nkn:preflightCacheTtlMs"));

        var fileTransferChunkPacingMs = FirstNonEmpty(
            Environment.GetEnvironmentVariable("NLINK_NKN_FILE_TRANSFER_CHUNK_PACING_MS"),
            appSettings.Get("NLINK_NKN_FILE_TRANSFER_CHUNK_PACING_MS"),
            appSettings.Get("nLink:nkn:fileTransferChunkPacingMs"));

        return new NknTransportOptions
        {
            SeedRpc = seedRpc,
            Identifier = identifier,
            KeyPath = ResolveKeyPath(configuredKeyPath),
            PreflightRpcEnabled = ParseBool(preflightRpcEnabled, defaultValue: false),
            PreflightTimeoutMs = ParseInt(preflightTimeoutMs, defaultValue: 700, minValue: 1, maxValue: 60_000),
            PreflightConcurrency = ParseInt(preflightConcurrency, defaultValue: 8, minValue: 1, maxValue: 256),
            PreflightCacheTtlMs = ParseInt(preflightCacheTtlMs, defaultValue: 600_000, minValue: 0, maxValue: 86_400_000),
            FileTransferChunkPacingMs = ParseInt(fileTransferChunkPacingMs, defaultValue: 2, minValue: 0, maxValue: 1_000),
        };
    }

    private static string ResolveKeyPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var localAppData = GetLocalAppDataPath();
        var defaultPath = Path.Combine(localAppData, "nLink", "identity.json");
        CleanupStalePerProcessIdentityFiles(defaultPath);

        if (!ShouldUsePerProcessLocalIdentity())
        {
            return defaultPath;
        }

        var directory = Path.GetDirectoryName(defaultPath)!;
        var fileName = $"identity.instance-{Environment.ProcessId}.json";
        return Path.Combine(directory, fileName);
    }

    private static bool ShouldUsePerProcessLocalIdentity()
    {
        if (shouldUsePerProcessLocalIdentityOverrideForTests is not null)
        {
            return shouldUsePerProcessLocalIdentityOverrideForTests();
        }

        try
        {
            using var current = Process.GetCurrentProcess();
            var processName = current.ProcessName;
            if (string.IsNullOrWhiteSpace(processName))
            {
                return false;
            }

            var others = Process.GetProcessesByName(processName);
            try
            {
                foreach (var process in others)
                {
                    try
                    {
                        if (process.Id != current.Id)
                        {
                            return true;
                        }
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            finally
            {
                Array.Clear(others, 0, others.Length);
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string GetLocalAppDataPath()
    {
        return localAppDataPathOverrideForTests?.Invoke()
               ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }

    private static void CleanupStalePerProcessIdentityFiles(string defaultKeyPath)
    {
        var directory = Path.GetDirectoryName(defaultKeyPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        var scanned = 0;
        var staleFound = 0;
        var deleted = 0;
        var failed = 0;

        foreach (var identityPath in EnumerateInstanceIdentityFiles(directory))
        {
            scanned++;
            if (!TryParseInstanceIdentityPid(identityPath, out var pid))
            {
                continue;
            }

            if (IsProcessRunning(pid))
            {
                continue;
            }

            staleFound++;
            deleted += TryDeleteFile(identityPath, ref failed);
            deleted += TryDeleteFile(NknSecretStore.GetSecretPath(identityPath), ref failed);
        }

        if (scanned == 0 && staleFound == 0 && deleted == 0 && failed == 0)
        {
            return;
        }

        var reason = failed > 0
            ? "stale_instance_cleanup_partial"
            : deleted > 0
                ? "stale_instance_cleanup_completed"
                : "stale_instance_cleanup_noop";
        var outcome = failed > 0
            ? PersistenceDiagnosticOutcome.Partial
            : deleted > 0
                ? PersistenceDiagnosticOutcome.Partial
                : PersistenceDiagnosticOutcome.None;
        var severity = failed > 0
            ? PersistenceDiagnosticSeverity.Warning
            : PersistenceDiagnosticSeverity.Info;
        var message =
            $"event=stale_instance_identity_cleanup; scanned={scanned}; stale_found={staleFound}; deleted={deleted}; failed={failed}; directory={directory}";

        if (failed > 0)
        {
            LocalOperationalLog.Warn("NKN.IdentityCleanup", message);
        }
        else
        {
            LocalOperationalLog.Info("NKN.IdentityCleanup", message);
        }

        PersistenceDiagnostics.Record(
            domain: "nkn_identity_store",
            operation: "cleanup_stale_instance_identities",
            severity: severity,
            outcome: outcome,
            reason: reason);
    }

    private static IEnumerable<string> EnumerateInstanceIdentityFiles(string directory)
    {
        if (enumerateInstanceIdentityFilesOverrideForTests is not null)
        {
            return enumerateInstanceIdentityFilesOverrideForTests(directory);
        }

        return Directory.GetFiles(directory, "identity.instance-*.json", SearchOption.TopDirectoryOnly);
    }

    private static bool TryParseInstanceIdentityPid(string identityPath, out int pid)
    {
        pid = 0;
        var fileName = Path.GetFileName(identityPath);
        const string prefix = "identity.instance-";
        const string suffix = ".json";
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var pidSpan = fileName.AsSpan(prefix.Length, fileName.Length - prefix.Length - suffix.Length);
        return int.TryParse(pidSpan, out pid) && pid > 0;
    }

    private static bool IsProcessRunning(int pid)
    {
        if (isProcessRunningOverrideForTests is not null)
        {
            return isProcessRunningOverrideForTests(pid);
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static int TryDeleteFile(string path, ref int failed)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        try
        {
            if (deleteFileOverrideForTests is not null)
            {
                deleteFileOverrideForTests(path);
            }
            else
            {
                File.Delete(path);
            }

            return 1;
        }
        catch (Exception ex)
        {
            failed++;
            LocalOperationalLog.Warn(
                "NKN.IdentityCleanup",
                $"event=stale_instance_identity_cleanup_delete_failed; path={path}; reason={ex.GetType().Name}");
            return 0;
        }
    }

    internal static IDisposable OverrideShouldUsePerProcessLocalIdentityForTests(bool value)
    {
        var previous = shouldUsePerProcessLocalIdentityOverrideForTests;
        shouldUsePerProcessLocalIdentityOverrideForTests = () => value;
        return new DelegateDisposable(() => shouldUsePerProcessLocalIdentityOverrideForTests = previous);
    }

    internal static IDisposable OverrideLocalAppDataPathForTests(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var previous = localAppDataPathOverrideForTests;
        localAppDataPathOverrideForTests = () => Path.GetFullPath(path);
        return new DelegateDisposable(() => localAppDataPathOverrideForTests = previous);
    }

    internal static IDisposable OverrideIsProcessRunningForTests(Func<int, bool> isProcessRunning)
    {
        ArgumentNullException.ThrowIfNull(isProcessRunning);
        var previous = isProcessRunningOverrideForTests;
        isProcessRunningOverrideForTests = isProcessRunning;
        return new DelegateDisposable(() => isProcessRunningOverrideForTests = previous);
    }

    internal static IDisposable OverrideDeleteFileForTests(Action<string> deleteFile)
    {
        ArgumentNullException.ThrowIfNull(deleteFile);
        var previous = deleteFileOverrideForTests;
        deleteFileOverrideForTests = deleteFile;
        return new DelegateDisposable(() => deleteFileOverrideForTests = previous);
    }

    internal static IDisposable OverrideEnumerateInstanceIdentityFilesForTests(Func<string, string[]> enumerateFiles)
    {
        ArgumentNullException.ThrowIfNull(enumerateFiles);
        var previous = enumerateInstanceIdentityFilesOverrideForTests;
        enumerateInstanceIdentityFilesOverrideForTests = enumerateFiles;
        return new DelegateDisposable(() => enumerateInstanceIdentityFilesOverrideForTests = previous);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static bool ParseBool(string? value, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        return defaultValue;
    }

    private static int ParseInt(string? value, int defaultValue, int minValue, int maxValue)
    {
        if (string.IsNullOrWhiteSpace(value) || !int.TryParse(value, out var parsed))
        {
            return defaultValue;
        }

        if (parsed < minValue)
        {
            return minValue;
        }

        if (parsed > maxValue)
        {
            return maxValue;
        }

        return parsed;
    }

    private sealed class AppSettingsJson
    {
        private readonly JsonElement? root;

        private AppSettingsJson(JsonElement? root)
        {
            this.root = root;
        }

        public static AppSettingsJson Load()
        {
            foreach (var path in new[]
                     {
                         Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                         Path.Combine(Environment.CurrentDirectory, "appsettings.json"),
                     })
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    using var stream = File.OpenRead(path);
                    using var doc = JsonDocument.Parse(stream);
                    return new AppSettingsJson(doc.RootElement.Clone());
                }
                catch
                {
                    // Ignore invalid config files and continue with defaults/env vars.
                }
            }

            return new AppSettingsJson(null);
        }

        public string? Get(string path)
        {
            if (root is null)
            {
                return null;
            }

            if (!path.Contains(':', StringComparison.Ordinal))
            {
                if (root.Value.TryGetProperty(path, out var flatValue) && flatValue.ValueKind == JsonValueKind.String)
                {
                    return flatValue.GetString();
                }

                return null;
            }

            var current = root.Value;
            foreach (var part in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                if (current.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                var matched = false;
                foreach (var prop in current.EnumerateObject())
                {
                    if (!string.Equals(prop.Name, part, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    current = prop.Value;
                    matched = true;
                    break;
                }

                if (!matched)
                {
                    return null;
                }
            }

            return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
        }
    }

    private sealed class DelegateDisposable(Action dispose) : IDisposable
    {
        private Action? disposeAction = dispose;

        public void Dispose()
        {
            Interlocked.Exchange(ref disposeAction, null)?.Invoke();
        }
    }
}
