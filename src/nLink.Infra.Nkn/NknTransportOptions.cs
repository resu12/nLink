using System;
using System.IO;
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

        return new NknTransportOptions
        {
            SeedRpc = seedRpc,
            Identifier = identifier,
            KeyPath = ResolveKeyPath(configuredKeyPath),
        };
    }

    private static string ResolveKeyPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "nLink", "identity.json");
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
