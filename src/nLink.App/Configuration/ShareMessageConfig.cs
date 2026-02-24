using System;
using System.IO;
using System.Text.Json;

namespace NLink.App.Configuration;

public sealed class ShareMessageConfig
{
    public ShareMessageConfig(string? downloadUrl)
    {
        DownloadUrl = string.IsNullOrWhiteSpace(downloadUrl) ? null : downloadUrl.Trim();
    }

    public string? DownloadUrl { get; }

    public static ShareMessageConfig Load()
    {
        var env = Environment.GetEnvironmentVariable("NLINK_DOWNLOAD_URL");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return new ShareMessageConfig(env);
        }

        var appSettingsValue = AppSettingsJson.TryGet("NLINK_DOWNLOAD_URL") ?? AppSettingsJson.TryGet("nLink:downloadUrl");
        return new ShareMessageConfig(appSettingsValue);
    }

    private static class AppSettingsJson
    {
        public static string? TryGet(string path)
        {
            foreach (var filePath in new[]
                     {
                         Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                         Path.Combine(Environment.CurrentDirectory, "appsettings.json"),
                     })
            {
                try
                {
                    if (!File.Exists(filePath))
                    {
                        continue;
                    }

                    using var stream = File.OpenRead(filePath);
                    using var doc = JsonDocument.Parse(stream);
                    return ResolvePath(doc.RootElement, path);
                }
                catch
                {
                    // Ignore invalid config files and continue with defaults.
                }
            }

            return null;
        }

        private static string? ResolvePath(JsonElement root, string path)
        {
            if (!path.Contains(':', StringComparison.Ordinal))
            {
                foreach (var property in root.EnumerateObject())
                {
                    if (string.Equals(property.Name, path, StringComparison.OrdinalIgnoreCase) &&
                        property.Value.ValueKind == JsonValueKind.String)
                    {
                        return property.Value.GetString();
                    }
                }

                return null;
            }

            var current = root;
            foreach (var part in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                if (current.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                var found = false;
                foreach (var property in current.EnumerateObject())
                {
                    if (!string.Equals(property.Name, part, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    current = property.Value;
                    found = true;
                    break;
                }

                if (!found)
                {
                    return null;
                }
            }

            return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
        }
    }
}
