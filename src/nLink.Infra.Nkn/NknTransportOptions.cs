using System.Diagnostics;
using System.Text.Json;

namespace NLink.Infra.Nkn;

internal sealed class NknTransportOptions
{
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

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var defaultPath = Path.Combine(localAppData, "nLink", "identity.json");

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
}
