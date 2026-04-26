using System.Diagnostics;

namespace NLink.SmokeTests;

[Trait("Area", "ScreenShare")]
[Collection(AvaloniaHeadlessUiCollection.Name)]
public sealed class ScreenShareLatencyRegressionAnalysisTests
{
    [Fact]
    public async Task AnalyzeScreenShareLatencyRegression_WithInEnvelopeCandidate_ReportsNoMaterialRegression()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-latency-analysis", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var references = new[]
            {
                CreateArtifact(tempRoot, "ref-a", 320, 320.0, 430.0, 0.99, 3, "visible_stable", "none", "normal", "none", "none", 1, 0, 0, 0, 0, 0, 0, "none", 20),
                CreateArtifact(tempRoot, "ref-b", 340, 330.0, 440.0, 0.99, 4, "visible_stable", "none", "normal", "none", "none", 1, 0, 0, 0, 0, 0, 0, "none", 22),
                CreateArtifact(tempRoot, "ref-c", 351, 338.0, 451.6, 0.98, 7, "visible_stable", "none", "normal", "none", "none", 1, 0, 0, 0, 0, 0, 0, "none", 24),
            };
            var candidate = CreateArtifact(tempRoot, "candidate-good", 330, 334.0, 442.0, 0.99, 5, "visible_stable", "none", "normal", "none", "none", 1, 0, 0, 0, 0, 0, 0, "none", 30);
            var logPath = CreateSyntheticLog(tempRoot, "good-log.txt", new[]
            {
                "[2026-04-22 20:00:07Z] [INFO] [ScreenShareTransport] event=screenshare_pressure_state_sent; session_id=sess-test; mode=reduce_fps; reason=none",
                "[2026-04-22 20:00:10Z] [INFO] [ScreenShareTransport] event=screenshare_pressure_state_sent; session_id=sess-test; mode=normal; reason=none"
            });

            var result = await RunScriptAsync(candidate, references, logPath);
            Assert.True(
                result.ExitCode == 0,
                $"ExitCode={result.ExitCode}{Environment.NewLine}STDOUT:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}STDERR:{Environment.NewLine}{result.StandardError}");
            Assert.Contains("comparison_status=within_reference_envelope", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("regression_classification=no_material_latency_regression", result.StandardOutput, StringComparison.Ordinal);

            var report = File.ReadAllText(Path.Combine(candidate, "latency-regression-analysis.txt"));
            Assert.Contains("candidate_avg_capture_to_render_ms=334.0", report, StringComparison.Ordinal);
            Assert.Contains("candidate_final_steady_state_tail_health_lines:", report, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task AnalyzeScreenShareLatencyRegression_WithBaselineLifecycleOutlier_ClassifiesPressureBaselineRegression()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-latency-analysis", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var references = new[]
            {
                CreateArtifact(tempRoot, "ref-a", 320, 320.0, 430.0, 0.99, 3, "recovering", "recovery_corridor", "catch_up", "none", "helper", 1, 0, 0, 0, 0, 0, 0, "none", 18),
                CreateArtifact(tempRoot, "ref-b", 340, 330.0, 440.0, 0.99, 4, "recovering", "recovery_corridor", "catch_up", "none", "helper", 1, 0, 0, 0, 0, 0, 0, "none", 20),
                CreateArtifact(tempRoot, "ref-c", 351, 338.0, 451.6, 0.98, 7, "recovering", "recovery_corridor", "catch_up", "none", "helper", 1, 0, 0, 0, 0, 0, 0, "none", 22),
            };
            var candidate = CreateArtifact(tempRoot, "candidate-bad", 866, 345.5, 402.5, 1.00, 2, "recovering", "recovery_corridor", "catch_up", "none", "helper", 1, 0, 13, 0, 13, 0, 74, "high_frame_age", 26);
            var logPath = CreateSyntheticLog(tempRoot, "bad-log.txt", new[]
            {
                "[2026-04-22 20:45:20Z] [INFO] [ScreenShareTransport] event=screenshare_pressure_state_sent; session_id=sess-test; mode=catch_up; reason=high_frame_age",
                "[2026-04-22 20:45:40Z] [INFO] [ScreenShareTransport] event=screenshare_pressure_state_sent; session_id=sess-test; mode=catch_up; reason=high_frame_age"
            });

            var result = await RunScriptAsync(candidate, references, logPath);
            Assert.True(
                result.ExitCode == 0,
                $"ExitCode={result.ExitCode}{Environment.NewLine}STDOUT:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}STDERR:{Environment.NewLine}{result.StandardError}");
            Assert.Contains("comparison_status=outside_reference_envelope", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("regression_classification=pressure_baseline_lifecycle_regression", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("classification_evidence=baseline_frozen_due_to_stall_count,cadence_stall_window_count,actionable_high_frame_age_count,dominant_helper_pressure_blocker", result.StandardOutput, StringComparison.Ordinal);

            var report = File.ReadAllText(Path.Combine(candidate, "latency-regression-analysis.txt"));
            Assert.Contains("candidate_baseline_capture_to_render_ms=866", report, StringComparison.Ordinal);
            Assert.Contains("candidate_actionable_high_frame_age_count=74", report, StringComparison.Ordinal);
            Assert.Contains("candidate_final_steady_state_tail_pressure_state_sent_lines:", report, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunScriptAsync(
        string candidateArtifactDir,
        IReadOnlyCollection<string> referenceArtifactDirs,
        string logPath)
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var scriptPath = Path.Combine(repoRoot, "tools", "Analyze-ScreenShareLatencyRegression.ps1");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = repoRoot,
            },
        };

        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-File");
        process.StartInfo.ArgumentList.Add(scriptPath);
        process.StartInfo.ArgumentList.Add("-CandidateArtifactDir");
        process.StartInfo.ArgumentList.Add(candidateArtifactDir);
        process.StartInfo.ArgumentList.Add("-LogPath");
        process.StartInfo.ArgumentList.Add(logPath);
        process.StartInfo.ArgumentList.Add("-ReferenceArtifactDirs");
        process.StartInfo.ArgumentList.Add(string.Join(",", referenceArtifactDirs));

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string CreateSyntheticLog(string root, string name, IReadOnlyCollection<string> lines)
    {
        var path = Path.Combine(root, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    private static string CreateArtifact(
        string root,
        string name,
        long baselineCaptureToRenderMs,
        double avgCaptureToRenderMs,
        double helperApplyMsAvg,
        double visibleApplyRatio,
        long reassemblerLossCount,
        string helperSessionPhase,
        string helperRecoveryMechanism,
        string senderOperatingState,
        string senderGuardState,
        string dominantTroubleDomain,
        long baselineEstablished,
        long baselineReseedInProgress,
        long baselineFrozenDueToStallCount,
        long baselineReseedAfterRecoveryCount,
        long cadenceStallWindowCount,
        long cadenceStallTriggerCount,
        long actionableHighFrameAgeCount,
        string dominantHelperPressureBlocker,
        int seedSeconds)
    {
        var artifactDir = Path.Combine(root, name);
        Directory.CreateDirectory(artifactDir);
        var baseTime = new DateTimeOffset(2026, 4, 22, 20, 0, seedSeconds, TimeSpan.Zero);

        File.WriteAllText(
            Path.Combine(artifactDir, "helper-quality-summary.txt"),
            string.Join(
                Environment.NewLine,
                new[]
                {
                    "log_path=" + Path.Combine(root, "synthetic.log"),
                    $"visible_apply_ratio={visibleApplyRatio.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}",
                    $"helper_apply_ms_avg={helperApplyMsAvg.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}",
                    $"reassembler_loss_count={reassemblerLossCount}",
                    $"baseline_capture_to_render_ms={baselineCaptureToRenderMs}",
                    "",
                    "helper_quality_summary_lines:",
                    BuildQualityLine(baseTime, "no_visible_baseline", "waiting_for_recovery_keyframe", avgCaptureToRenderMs - 20),
                    BuildQualityLine(baseTime.AddSeconds(2), "recovering", "recovery_corridor", avgCaptureToRenderMs - 10),
                    BuildQualityLine(baseTime.AddSeconds(4), "visible_stable", "none", avgCaptureToRenderMs),
                }));

        File.WriteAllText(
            Path.Combine(artifactDir, "helper-pressure-summary.txt"),
            string.Join(
                Environment.NewLine,
                new[]
                {
                    "log_path=" + Path.Combine(root, "synthetic.log"),
                    $"dominant_helper_pressure_blocker={dominantHelperPressureBlocker}",
                    $"baseline_established={baselineEstablished}",
                    $"baseline_capture_to_render_ms={baselineCaptureToRenderMs}",
                    "age_excess_ms=0",
                    "progress_stall_ms=0",
                    $"baseline_reseed_in_progress={baselineReseedInProgress}",
                    $"baseline_frozen_due_to_stall_count={baselineFrozenDueToStallCount}",
                    $"baseline_reseed_after_recovery_count={baselineReseedAfterRecoveryCount}",
                    $"cadence_stall_window_count={cadenceStallWindowCount}",
                    $"cadence_stall_trigger_count={cadenceStallTriggerCount}",
                    "steady_visible_progress_active=1",
                    $"actionable_high_frame_age_count={actionableHighFrameAgeCount}",
                    "",
                    "helper_pressure_summary_lines:",
                    BuildPressureLine(baseTime, "no_visible_baseline", 0, 0, "none", 0, 0, 0),
                    BuildPressureLine(baseTime.AddSeconds(2), "recovering", baselineCaptureToRenderMs, baselineFrozenDueToStallCount, dominantHelperPressureBlocker, cadenceStallWindowCount, cadenceStallTriggerCount, actionableHighFrameAgeCount),
                    BuildPressureLine(baseTime.AddSeconds(4), helperSessionPhase, baselineCaptureToRenderMs, baselineFrozenDueToStallCount, dominantHelperPressureBlocker, cadenceStallWindowCount, cadenceStallTriggerCount, actionableHighFrameAgeCount),
                }));

        File.WriteAllText(
            Path.Combine(artifactDir, "health-snapshot-summary.txt"),
            string.Join(
                Environment.NewLine,
                new[]
                {
                    "log_path=" + Path.Combine(root, "synthetic.log"),
                    $"sender_operating_state={senderOperatingState}",
                    $"sender_guard_state={senderGuardState}",
                    $"helper_session_phase={helperSessionPhase}",
                    $"helper_recovery_mechanism={helperRecoveryMechanism}",
                    "dominant_loss_class=benign_stale_cleanup",
                    $"dominant_pressure_blocker={dominantHelperPressureBlocker}",
                    $"dominant_trouble_domain={dominantTroubleDomain}",
                    "recovery_active=0",
                    $"baseline_established={baselineEstablished}",
                    "steady_visible_progress_active=1",
                    "",
                    "health_snapshot_lines:",
                    BuildHealthLine(baseTime, "reduced", "bootstrap_grace", "no_visible_baseline", "waiting_for_recovery_keyframe", "none", "sender"),
                    BuildHealthLine(baseTime.AddSeconds(2), senderOperatingState, senderGuardState, "recovering", "recovery_corridor", dominantHelperPressureBlocker, "helper"),
                    BuildHealthLine(baseTime.AddSeconds(4), senderOperatingState, senderGuardState, helperSessionPhase, helperRecoveryMechanism, dominantHelperPressureBlocker, dominantTroubleDomain),
                }));

        return artifactDir;
    }

    private static string BuildQualityLine(DateTimeOffset timestamp, string phase, string mechanism, double avgCaptureToRenderMs)
        => $"[{timestamp:yyyy-MM-dd HH:mm:ss}Z] [INFO] [ScreenShare] event=screenshare_helper_quality_summary; role=helper_remote; trigger=periodic; session_id=sess-test; helper_session_phase={phase}; helper_recovery_mechanism={mechanism}; dominant_loss_class=benign_stale_cleanup; baseline_established=1; steady_visible_progress_active=1; visible_apply_ratio=1.00; avg_capture_to_render_ms={avgCaptureToRenderMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}; actionable_late_fragment_count=0";

    private static string BuildPressureLine(DateTimeOffset timestamp, string phase, long baselineCaptureToRenderMs, long baselineFrozenDueToStallCount, string blocker, long cadenceStallWindowCount, long cadenceStallTriggerCount, long actionableHighFrameAgeCount)
        => $"[{timestamp:yyyy-MM-dd HH:mm:ss}Z] [INFO] [ScreenShareTransport] event=screenshare_helper_pressure_epoch_summary; role=helper_remote; reason=periodic; session_id=sess-test; helper_session_phase={phase}; baseline_established=1; baseline_capture_to_render_ms={baselineCaptureToRenderMs}; baseline_reseed_in_progress=0; baseline_frozen_due_to_stall_count={baselineFrozenDueToStallCount}; baseline_reseed_after_recovery_count=0; cadence_stall_window_count={cadenceStallWindowCount}; cadence_stall_trigger_count={cadenceStallTriggerCount}; actionable_high_frame_age_count={actionableHighFrameAgeCount}; dominant_pressure_blocker={blocker}";

    private static string BuildHealthLine(DateTimeOffset timestamp, string senderOperatingState, string senderGuardState, string helperSessionPhase, string helperRecoveryMechanism, string dominantPressureBlocker, string dominantTroubleDomain)
        => $"[{timestamp:yyyy-MM-dd HH:mm:ss}Z] [INFO] [ScreenShare] event=screenshare_health_snapshot; sender_operating_state={senderOperatingState}; sender_guard_state={senderGuardState}; helper_session_phase={helperSessionPhase}; helper_recovery_mechanism={helperRecoveryMechanism}; dominant_loss_class=benign_stale_cleanup; dominant_pressure_blocker={dominantPressureBlocker}; dominant_trouble_domain={dominantTroubleDomain}; recovery_active=0; baseline_established=1; steady_visible_progress_active=1";

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort test cleanup.
        }
    }
}
