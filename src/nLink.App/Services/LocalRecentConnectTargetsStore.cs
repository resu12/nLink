using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NLink.Core.Diagnostics;
using NLink.Core.SessionConnect;

namespace NLink.App.Services;

public sealed class LocalRecentConnectTargetsStore : IRecentConnectTargetsStore
{
    private const int CurrentVersion = 1;
    private const int MaxTargets = 8;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string filePath;

    public LocalRecentConnectTargetsStore(string? customPath = null)
    {
        filePath = string.IsNullOrWhiteSpace(customPath) ? BuildDefaultPath() : customPath;
    }

    public IReadOnlyList<string> LoadTargets()
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return Array.Empty<string>();
            }

            var json = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return Array.Empty<string>();
            }

            var parsed = JsonSerializer.Deserialize<RecentConnectTargetsDocument>(json, JsonOptions);
            if (parsed?.Targets is null || parsed.Targets.Count == 0)
            {
                return Array.Empty<string>();
            }

            var sanitized = SanitizeTargets(parsed.Targets);
            return sanitized;
        }
        catch (Exception ex)
        {
            PersistenceDiagnostics.Record(
                domain: "recent_connect_targets",
                operation: "load",
                severity: PersistenceDiagnosticSeverity.Warning,
                outcome: PersistenceDiagnosticOutcome.Fallback,
                reason: ex.GetType().Name,
                userWarning: "Recent targets could not be loaded.");
            return Array.Empty<string>();
        }
    }

    public void SaveTargets(IReadOnlyList<string> targets)
    {
        try
        {
            var sanitized = SanitizeTargets(targets);
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var document = new RecentConnectTargetsDocument
            {
                Version = CurrentVersion,
                Targets = sanitized.ToList(),
            };
            var json = JsonSerializer.Serialize(document, JsonOptions);
            var tempPath = filePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Copy(tempPath, filePath, overwrite: true);
            File.Delete(tempPath);
        }
        catch (Exception ex)
        {
            PersistenceDiagnostics.Record(
                domain: "recent_connect_targets",
                operation: "save",
                severity: PersistenceDiagnosticSeverity.Warning,
                outcome: PersistenceDiagnosticOutcome.Fallback,
                reason: ex.GetType().Name,
                userWarning: "Recent targets could not be saved.");
        }
    }

    private static IReadOnlyList<string> SanitizeTargets(IReadOnlyList<string> targets)
    {
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var output = new List<string>(Math.Min(targets.Count, MaxTargets));
        foreach (var raw in targets)
        {
            if (output.Count >= MaxTargets)
            {
                break;
            }

            var candidate = raw?.Trim();
            if (string.IsNullOrWhiteSpace(candidate) ||
                !PeerAddress.TryParse(candidate, out var parsed) ||
                !unique.Add(parsed.Value))
            {
                continue;
            }

            output.Add(parsed.Value);
        }

        return output;
    }

    private static string BuildDefaultPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "nLink", "settings", "recent-connect-targets.json");
    }

    private sealed class RecentConnectTargetsDocument
    {
        public int Version { get; set; } = CurrentVersion;

        public List<string> Targets { get; set; } = new();
    }
}
