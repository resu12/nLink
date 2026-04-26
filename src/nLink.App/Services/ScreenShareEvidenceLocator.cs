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
    string DeepestTrackBClassification,
    string QualityProfileSummary,
    string PerformanceSummary,
    string CursorSummary,
    string VisualSafetySummary,
    string LowFpsSummary,
    string ExternalTopologySummary)
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
            $"screenshare_quality_profile: {QualityProfileSummary}",
            $"screenshare_performance_summary: {PerformanceSummary}",
            $"screenshare_cursor_summary: {CursorSummary}",
            $"screenshare_visual_safety_summary: {VisualSafetySummary}",
            $"screenshare_low_fps_summary: {LowFpsSummary}",
            $"screenshare_external_topology_summary: {ExternalTopologySummary}",
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
        "(none)",
        "(none)",
        "(none)",
        "(none)",
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
        "(none)",
        "(none)",
        "(none)",
        "(none)",
        "(none)",
        "(none)",
        "(none)");

    private static ScreenShareEvidenceSnapshot BuildVerdictSnapshot(DirectoryInfo artifact, string verdictPath)
    {
        var values = ParseKeyValueFile(verdictPath);
        MergeMissing(values, ParseOptionalKeyValueFile(artifact, "quality-presentation-summary.txt"));
        MergeMissing(values, ParseOptionalKeyValueFile(artifact, "helper-quality-summary.txt"));
        MergeMissing(values, ParseOptionalKeyValueFile(artifact, "external-topology-summary.txt"));
        MergeMissing(values, ParseOptionalKeyValueFile(artifact, "low-fps-catch-up-summary.txt"));

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
            GetValue(values, "deepest_track_b_classification", "(none)"),
            BuildQualityProfileSummary(values),
            BuildPerformanceSummary(values),
            BuildCursorSummary(values),
            BuildVisualSafetySummary(values),
            BuildLowFpsSummary(values),
            BuildExternalTopologySummary(values));
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

    private static Dictionary<string, string> ParseOptionalKeyValueFile(DirectoryInfo artifact, string fileName)
    {
        var path = Path.Combine(artifact.FullName, fileName);
        return File.Exists(path)
            ? ParseKeyValueFile(path)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static void MergeMissing(Dictionary<string, string> target, IReadOnlyDictionary<string, string> fallback)
    {
        foreach (var pair in fallback)
        {
            if (!target.ContainsKey(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            {
                target[pair.Key] = pair.Value;
            }
        }
    }

    private static string GetValue(IReadOnlyDictionary<string, string> values, string key, string fallback)
    {
        return values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;
    }

    private static string GetAnyValue(IReadOnlyDictionary<string, string> values, string fallback, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = GetValue(values, key, string.Empty);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return fallback;
    }

    private static string BuildQualityProfileSummary(IReadOnlyDictionary<string, string> values)
    {
        var width = GetAnyValue(values, "(none)", "quality_active_encode_target_width", "active_encode_target_width");
        var height = GetAnyValue(values, "(none)", "quality_active_encode_target_height", "active_encode_target_height");
        var fps = GetAnyValue(values, "(none)", "quality_active_encode_target_fps", "active_encode_target_fps");
        var bitrate = GetAnyValue(values, "(none)", "quality_active_encode_target_bitrate", "active_encode_target_bitrate");
        var profile = GetAnyValue(values, "(none)", "quality_encoder_profile", "encoder_profile");
        var mode = GetAnyValue(values, "(none)", "quality_sender_freshness_mode", "sender_freshness_mode");
        var preset = GetAnyValue(values, "(none)", "quality_effective_quality_preset", "effective_quality_preset");

        if (width == "(none)" && height == "(none)" && fps == "(none)" && profile == "(none)")
        {
            return "(none)";
        }

        return $"profile={profile}; mode={mode}; target={width}x{height}@{fps}fps; bitrate={bitrate}; preset={preset}";
    }

    private static string BuildPerformanceSummary(IReadOnlyDictionary<string, string> values)
    {
        var encodedFps = GetAnyValue(values, "(none)", "actual_encoded_displayable_fps");
        var readbackFps = GetAnyValue(values, "(none)", "raw_source_readback_fps");
        var cpu = GetAnyValue(values, "(none)", "sender_process_cpu_percent");
        var preprocess = GetAnyValue(values, "(none)", "last_preprocess_duration_ms");
        var gpuScale = GetAnyValue(values, "(none)", "raw_source_gpu_scale_enabled");
        var resizePath = GetAnyValue(values, "(none)", "preprocess_resize_path");

        if (encodedFps == "(none)" && readbackFps == "(none)" && cpu == "(none)" && preprocess == "(none)")
        {
            return "(none)";
        }

        return $"encoded_fps={encodedFps}; readback_fps={readbackFps}; sender_cpu_pct={cpu}; preprocess_ms={preprocess}; gpu_scale={gpuScale}; resize_path={resizePath}";
    }

    private static string BuildCursorSummary(IReadOnlyDictionary<string, string> values)
    {
        var mode = GetAnyValue(values, "(none)", "cursor_delivery_mode");
        var desired = GetAnyValue(values, "(none)", "cursor_capture_desired_enabled");
        var applied = GetAnyValue(values, "(none)", "cursor_capture_enabled");
        var status = GetAnyValue(values, "(none)", "cursor_capture_apply_status", "cursor_overlay_last_status");

        if (mode == "(none)" && desired == "(none)" && applied == "(none)")
        {
            return "(none)";
        }

        return $"mode={mode}; capture_desired={desired}; capture_enabled={applied}; status={status}";
    }

    private static string BuildVisualSafetySummary(IReadOnlyDictionary<string, string> values)
    {
        var unsafeTail = GetAnyValue(values, "(none)", "pre_candidate_gap_tail_emitted_to_viewer_count");
        var late = GetAnyValue(values, "(none)", "actionable_late_fragment_count");
        var taint = GetAnyValue(values, "(none)", "h264_reference_taint_active");
        var quarantine = GetAnyValue(values, "(none)", "h264_reference_quarantine_active");
        var taintReason = GetAnyValue(values, "(none)", "h264_reference_taint_last_reason");

        if (unsafeTail == "(none)" && late == "(none)" && taint == "(none)" && quarantine == "(none)")
        {
            return "(none)";
        }

        return $"unsafe_tail={unsafeTail}; actionable_late={late}; h264_taint={taint}; h264_quarantine={quarantine}; taint_reason={taintReason}";
    }

    private static string BuildLowFpsSummary(IReadOnlyDictionary<string, string> values)
    {
        var classification = GetAnyValue(values, "(none)", "low_fps_catch_up_classification", "classification");
        var applyFps = GetAnyValue(values, "(none)", "low_fps_effective_apply_fps", "effective_apply_fps");
        var modes = GetAnyValue(values, "(none)", "low_fps_sender_mode_counts", "sender_mode_counts");

        if (classification == "(none)" && applyFps == "(none)" && modes == "(none)")
        {
            return "(none)";
        }

        return $"classification={classification}; apply_fps={applyFps}; modes={modes}";
    }

    private static string BuildExternalTopologySummary(IReadOnlyDictionary<string, string> values)
    {
        var profile = GetAnyValue(values, "(none)", "external_topology_profile");
        var rpc = GetAnyValue(values, "(none)", "external_topology_selected_rpc_key", "selected_rpc_key");
        var mediaSubclients = GetAnyValue(values, "(none)", "external_topology_media_subclients", "media_subclients");
        var classification = GetAnyValue(values, "(none)", "external_topology_classification");

        if (profile == "(none)" && rpc == "(none)" && mediaSubclients == "(none)")
        {
            return "(none)";
        }

        return $"profile={profile}; rpc={rpc}; media_subclients={mediaSubclients}; classification={classification}";
    }

    private static string RedactArtifactPath(string path)
        => DiagnosticsExportBuilder.RedactStructuredValue("screenshare_artifact_dir", path);

    private static string RedactVerdictPath(string path)
        => DiagnosticsExportBuilder.RedactStructuredValue("screenshare_verdict_path", path);
}
