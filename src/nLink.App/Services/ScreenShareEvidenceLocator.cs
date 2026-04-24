using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NLink.App.Services;

internal enum ScreenShareEvidenceStatus
{
    NoneFound,
    ArtifactWithoutVerdict,
    VerdictAvailable,
}

internal sealed record ScreenShareEvidenceSnapshot(
    ScreenShareEvidenceStatus Status,
    string StatusKey,
    string ArtifactName,
    string ArtifactDirectory,
    string RedactedArtifactDirectory,
    string VerdictPath,
    string RedactedVerdictPath,
    string OperatorVerdict,
    string OperatorSummary,
    string NextOperatorAction,
    string MissingRequiredInputs,
    string DeepestTrackBStage,
    string DeepestTrackBClassification)
{
    public string ToReportText()
    {
        var lines = new[]
        {
            "Screenshare evidence",
            "--------------------",
            $"screenshare_evidence_status: {StatusKey}",
            $"screenshare_artifact_name: {ArtifactName}",
            $"screenshare_artifact_dir: {RedactedArtifactDirectory}",
            $"screenshare_verdict_path: {RedactedVerdictPath}",
            $"screenshare_operator_verdict: {OperatorVerdict}",
            $"screenshare_operator_summary: {OperatorSummary}",
            $"screenshare_next_operator_action: {NextOperatorAction}",
            $"screenshare_missing_required_inputs: {MissingRequiredInputs}",
            $"screenshare_deepest_stage: {DeepestTrackBStage}",
            $"screenshare_deepest_classification: {DeepestTrackBClassification}",
        };

        return string.Join(Environment.NewLine, lines);
    }

    internal static ScreenShareEvidenceSnapshot NoneFound() => new(
        ScreenShareEvidenceStatus.NoneFound,
        "none_found",
        "(none)",
        "(none)",
        "(none)",
        "(none)",
        "(none)",
        "(none)",
        "No screenshare soak artifact was found.",
        "Use ScreenShare-Ops.ps1 -Mode NknSoak when live screenshare evidence is needed, then run -Mode AnalyzeRetained on the artifact.",
        "(none)",
        "(none)",
        "(none)");
}

internal sealed class ScreenShareEvidenceLocator
{
    internal const string EvidenceRootEnvVar = "NLINK_SCREENSHARE_EVIDENCE_ROOT";
    private const string VerdictFileName = "screenshare-operator-verdict.txt";

    private readonly string[] evidenceRoots;

    public ScreenShareEvidenceLocator(IEnumerable<string> evidenceRoots)
    {
        ArgumentNullException.ThrowIfNull(evidenceRoots);
        this.evidenceRoots = evidenceRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static ScreenShareEvidenceLocator CreateDefault()
    {
        var overrideRoot = Environment.GetEnvironmentVariable(EvidenceRootEnvVar);
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            return new ScreenShareEvidenceLocator([overrideRoot]);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new ScreenShareEvidenceLocator(
        [
            Path.Combine(Environment.CurrentDirectory, "artifacts", "soak"),
            Path.Combine(localAppData, "nLink", "artifacts", "soak")
        ]);
    }

    public ScreenShareEvidenceSnapshot ReadLatest()
    {
        var artifact = FindLatestArtifactDirectory();
        if (artifact is null)
        {
            return ScreenShareEvidenceSnapshot.NoneFound();
        }

        var verdictPath = Path.Combine(artifact.FullName, VerdictFileName);
        if (!File.Exists(verdictPath))
        {
            return BuildArtifactWithoutVerdictSnapshot(artifact, verdictPath);
        }

        return BuildVerdictSnapshot(artifact, verdictPath);
    }

    private DirectoryInfo? FindLatestArtifactDirectory()
    {
        var candidates = new List<DirectoryInfo>();
        foreach (var rootPath in evidenceRoots)
        {
            try
            {
                if (!Directory.Exists(rootPath))
                {
                    continue;
                }

                var root = new DirectoryInfo(rootPath);
                if (LooksLikeSoakArtifact(root))
                {
                    candidates.Add(root);
                }

                candidates.AddRange(root.EnumerateDirectories().Where(LooksLikeSoakArtifact));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException or DirectoryNotFoundException)
            {
                // Diagnostics must remain best-effort and must not fail because a support path is unavailable.
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.Name, StringComparer.Ordinal)
            .ThenByDescending(candidate => candidate.LastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static bool LooksLikeSoakArtifact(DirectoryInfo directory)
    {
        var knownFiles = new[]
        {
            VerdictFileName,
            "stability-gates-summary.txt",
            "transport-mode-summary.txt",
            "helper-socket-receive-summary.txt",
            "helper-external-transport-health-analysis.txt",
        };

        return knownFiles.Any(fileName => File.Exists(Path.Combine(directory.FullName, fileName)));
    }

    private static ScreenShareEvidenceSnapshot BuildArtifactWithoutVerdictSnapshot(DirectoryInfo artifact, string verdictPath) => new(
        ScreenShareEvidenceStatus.ArtifactWithoutVerdict,
        "artifact_without_verdict",
        artifact.Name,
        artifact.FullName,
        RedactArtifactPath(artifact.FullName),
        verdictPath,
        RedactVerdictPath(verdictPath),
        "(missing)",
        "Latest screenshare artifact exists, but screenshare-operator-verdict.txt is missing.",
        "Run ScreenShare-Ops.ps1 -Mode AnalyzeRetained for the latest artifact before sharing support evidence.",
        VerdictFileName,
        "(none)",
        "(none)");

    private static ScreenShareEvidenceSnapshot BuildVerdictSnapshot(DirectoryInfo artifact, string verdictPath)
    {
        var values = ParseKeyValueFile(verdictPath);
        return new ScreenShareEvidenceSnapshot(
            ScreenShareEvidenceStatus.VerdictAvailable,
            "verdict_available",
            artifact.Name,
            artifact.FullName,
            RedactArtifactPath(artifact.FullName),
            verdictPath,
            RedactVerdictPath(verdictPath),
            GetValue(values, "operator_verdict", "(unknown)"),
            DiagnosticsExportBuilder.RedactFreeForm(GetValue(values, "operator_summary", "(none)")),
            DiagnosticsExportBuilder.RedactFreeForm(GetValue(values, "next_operator_action", "(none)")),
            GetValue(values, "missing_required_inputs", "(none)"),
            GetValue(values, "deepest_track_b_stage", "(none)"),
            GetValue(values, "deepest_track_b_classification", "(none)"));
    }

    private static Dictionary<string, string> ParseKeyValueFile(string path)
    {
        try
        {
            return File.ReadLines(path)
                .Select(line => line.Split('=', 2))
                .Where(parts => parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
                .ToDictionary(
                    parts => parts[0].Trim(),
                    parts => parts[1].Trim(),
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["operator_verdict"] = "(unreadable)",
                ["operator_summary"] = "Could not read screenshare-operator-verdict.txt.",
                ["next_operator_action"] = "Regenerate or re-run AnalyzeRetained for the artifact.",
            };
        }
    }

    private static string GetValue(IReadOnlyDictionary<string, string> values, string key, string fallback)
    {
        return values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;
    }

    private static string RedactArtifactPath(string path)
        => DiagnosticsExportBuilder.RedactStructuredValue("screenshare_artifact_dir", path);

    private static string RedactVerdictPath(string path)
        => DiagnosticsExportBuilder.RedactStructuredValue("screenshare_verdict_path", path);
}
