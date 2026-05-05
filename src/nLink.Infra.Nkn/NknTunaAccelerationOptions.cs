using System.Text.Json;
using NLink.Core.Configuration;

namespace NLink.Infra.Nkn;

internal sealed class NknTunaAccelerationOptions
{
    public static NknTunaAccelerationOptions Disabled { get; } = new();

    private NknTunaAccelerationOptions()
    {
    }

    public bool Enabled { get; private init; }

    public string? SidecarExePath { get; private init; }

    public string? ListenerEndpoint { get; private init; }

    public NknAccelerationLaneKind Lanes { get; private init; } =
        NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen;

    public int QueueCapacity { get; private init; } = 256;

    public int ConnectTimeoutMs { get; private init; } = 5_000;

    public int DialerReadyTimeoutMs { get; private init; } = 30_000;

    public int TunaDialTimeoutMs { get; private init; } = 25_000;

    public string? DialerSeedBase64 { get; private init; }

    public static NknTunaAccelerationOptions Load()
    {
        var appSettings = AppSettingsJson.Load();
        var enabled = FirstNonEmpty(
            ReleaseOverridePolicy.ReadUnsafeEnvironmentVariable("NLINK_NKN_TUNA_ENABLED", category: "nkn_tuna"),
            ReleaseOverridePolicy.ApplyUnsafeAppSetting("NLINK_NKN_TUNA_ENABLED", appSettings.Get("NLINK_NKN_TUNA_ENABLED"), category: "nkn_tuna"),
            ReleaseOverridePolicy.ApplyUnsafeAppSetting("nLink:nkn:tuna:enabled", appSettings.Get("nLink:nkn:tuna:enabled"), category: "nkn_tuna"));
        var sidecarExe = FirstNonEmpty(
            ReleaseOverridePolicy.ReadUnsafeEnvironmentVariable("NLINK_NKN_TUNA_SIDECAR_EXE", category: "nkn_tuna"),
            ReleaseOverridePolicy.ApplyUnsafeAppSetting("NLINK_NKN_TUNA_SIDECAR_EXE", appSettings.Get("NLINK_NKN_TUNA_SIDECAR_EXE"), category: "nkn_tuna"),
            ReleaseOverridePolicy.ApplyUnsafeAppSetting("nLink:nkn:tuna:sidecarExe", appSettings.Get("nLink:nkn:tuna:sidecarExe"), category: "nkn_tuna"));
        var listenerEndpoint = FirstNonEmpty(
            ReleaseOverridePolicy.ReadUnsafeEnvironmentVariable("NLINK_NKN_TUNA_LISTENER_ENDPOINT", category: "nkn_tuna"),
            ReleaseOverridePolicy.ApplyUnsafeAppSetting("NLINK_NKN_TUNA_LISTENER_ENDPOINT", appSettings.Get("NLINK_NKN_TUNA_LISTENER_ENDPOINT"), category: "nkn_tuna"),
            ReleaseOverridePolicy.ApplyUnsafeAppSetting("nLink:nkn:tuna:listenerEndpoint", appSettings.Get("nLink:nkn:tuna:listenerEndpoint"), category: "nkn_tuna"));
        var lanes = FirstNonEmpty(
            ReleaseOverridePolicy.ReadUnsafeEnvironmentVariable("NLINK_NKN_TUNA_LANES", category: "nkn_tuna"),
            ReleaseOverridePolicy.ApplyUnsafeAppSetting("NLINK_NKN_TUNA_LANES", appSettings.Get("NLINK_NKN_TUNA_LANES"), category: "nkn_tuna"),
            ReleaseOverridePolicy.ApplyUnsafeAppSetting("nLink:nkn:tuna:lanes", appSettings.Get("nLink:nkn:tuna:lanes"), category: "nkn_tuna"));

        return new NknTunaAccelerationOptions
        {
            Enabled = ParseBool(enabled, defaultValue: false),
            SidecarExePath = NormalizePathOrNull(sidecarExe),
            ListenerEndpoint = string.IsNullOrWhiteSpace(listenerEndpoint) ? null : listenerEndpoint.Trim(),
            Lanes = ParseLanes(lanes),
        };
    }

    private static string? NormalizePathOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(value.Trim());
        }
        catch
        {
            return null;
        }
    }

    private static NknAccelerationLaneKind ParseLanes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen;
        }

        var lanes = NknAccelerationLaneCodec.FromNames(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return lanes == NknAccelerationLaneKind.None
            ? NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen
            : lanes;
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

        var trimmed = value.Trim();
        if (string.Equals(trimmed, "1", StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(trimmed, "0", StringComparison.Ordinal))
        {
            return false;
        }

        return defaultValue;
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
                    // Ignore invalid config files and keep the spike opt-in only.
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
                return root.Value.TryGetProperty(path, out var flatValue) && flatValue.ValueKind == JsonValueKind.String
                    ? flatValue.GetString()
                    : null;
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
