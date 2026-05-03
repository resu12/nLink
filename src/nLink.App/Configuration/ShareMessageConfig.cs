using System;
using System.IO;
using System.Text.Json;
using NLink.Core.Configuration;

namespace NLink.App.Configuration;

public sealed class ShareMessageConfig
{
    private const string DefaultRepoUrl = "https://github.com/resu12/nLink";

    public ShareMessageConfig(string? downloadUrl)
        : this(downloadUrl, null)
    {
    }

    public ShareMessageConfig(string? downloadUrl, string? repoUrl)
    {
        DownloadUrl = string.IsNullOrWhiteSpace(downloadUrl) ? null : downloadUrl.Trim();
        HasConfiguredRepoUrl = !string.IsNullOrWhiteSpace(repoUrl);
        RepoUrl = string.IsNullOrWhiteSpace(repoUrl) ? DefaultRepoUrl : repoUrl.Trim().TrimEnd('/');
    }

    public string? DownloadUrl { get; }

    public bool HasConfiguredRepoUrl { get; }

    public string RepoUrl { get; }

    public string ReleasesUrl => $"{RepoUrl}/releases";

    public string? BugReportUrl => HasConfiguredRepoUrl
        ? $"{RepoUrl}/issues/new?template=bug_report.yml"
        : null;

    public static ShareMessageConfig Load()
    {
        var env = ReleaseOverridePolicy.ReadUnsafeEnvironmentVariable("NLINK_DOWNLOAD_URL", category: "release_content_link");
        if (!string.IsNullOrWhiteSpace(env))
        {
            var repoEnv = ReleaseOverridePolicy.ReadUnsafeEnvironmentVariable("NLINK_REPO_URL", category: "release_content_link");
            return new ShareMessageConfig(env, repoEnv);
        }

        var appSettingsValue =
            ReleaseOverridePolicy.ApplyUnsafeAppSetting("NLINK_DOWNLOAD_URL", AppSettingsJson.TryGet("NLINK_DOWNLOAD_URL"), category: "release_content_link")
            ?? ReleaseOverridePolicy.ApplyUnsafeAppSetting("nLink:downloadUrl", AppSettingsJson.TryGet("nLink:downloadUrl"), category: "release_content_link");
        var repoValue =
            ReleaseOverridePolicy.ReadUnsafeEnvironmentVariable("NLINK_REPO_URL", category: "release_content_link")
            ?? ReleaseOverridePolicy.ApplyUnsafeAppSetting("NLINK_REPO_URL", AppSettingsJson.TryGet("NLINK_REPO_URL"), category: "release_content_link")
            ?? ReleaseOverridePolicy.ApplyUnsafeAppSetting("nLink:repoUrl", AppSettingsJson.TryGet("nLink:repoUrl"), category: "release_content_link");
        return new ShareMessageConfig(appSettingsValue, repoValue);
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
