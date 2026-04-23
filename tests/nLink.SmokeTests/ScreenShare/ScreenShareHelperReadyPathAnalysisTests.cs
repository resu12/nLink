using System.Diagnostics;

namespace NLink.SmokeTests;

[Trait("Area", "ScreenShare")]
public sealed class ScreenShareHelperReadyPathAnalysisTests
{
    [Fact]
    public async Task AnalyzeHelperReadyPath_ClassifiesReceiveArrivalLatency()
    {
        await RunClassificationCaseAsync(
            candidateCaptureToFirstFragmentObservedMedianMs: 180,
            candidateFirstFragmentToLastFragmentObservedMedianMs: 20,
            candidateLastFragmentToAssemblyCompleteMedianMs: 10,
            candidateAssemblyCompleteToFrameEmittedMedianMs: 8,
            expectedClassification: "receive_arrival_latency");
    }

    [Fact]
    public async Task AnalyzeHelperReadyPath_ClassifiesFragmentCompletionLatency()
    {
        await RunClassificationCaseAsync(
            candidateCaptureToFirstFragmentObservedMedianMs: 35,
            candidateFirstFragmentToLastFragmentObservedMedianMs: 150,
            candidateLastFragmentToAssemblyCompleteMedianMs: 12,
            candidateAssemblyCompleteToFrameEmittedMedianMs: 9,
            expectedClassification: "fragment_completion_latency");
    }

    [Fact]
    public async Task AnalyzeHelperReadyPath_ClassifiesAssemblyLatency()
    {
        await RunClassificationCaseAsync(
            candidateCaptureToFirstFragmentObservedMedianMs: 35,
            candidateFirstFragmentToLastFragmentObservedMedianMs: 25,
            candidateLastFragmentToAssemblyCompleteMedianMs: 170,
            candidateAssemblyCompleteToFrameEmittedMedianMs: 7,
            expectedClassification: "assembly_latency");
    }

    [Fact]
    public async Task AnalyzeHelperReadyPath_ClassifiesReadyEmitLatency()
    {
        await RunClassificationCaseAsync(
            candidateCaptureToFirstFragmentObservedMedianMs: 30,
            candidateFirstFragmentToLastFragmentObservedMedianMs: 18,
            candidateLastFragmentToAssemblyCompleteMedianMs: 12,
            candidateAssemblyCompleteToFrameEmittedMedianMs: 140,
            expectedClassification: "ready_emit_latency");
    }

    [Fact]
    public async Task AnalyzeHelperReadyPath_ClassifiesMixedWhenNoDominantStage()
    {
        await RunClassificationCaseAsync(
            candidateCaptureToFirstFragmentObservedMedianMs: 70,
            candidateFirstFragmentToLastFragmentObservedMedianMs: 60,
            candidateLastFragmentToAssemblyCompleteMedianMs: 55,
            candidateAssemblyCompleteToFrameEmittedMedianMs: 50,
            expectedClassification: "mixed_or_inconclusive");
    }

    [Fact]
    public async Task AnalyzeHelperReadyPath_FallsBackToCandidateCompositionWhenReferenceSummariesAreMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-ready-path-analysis", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var candidate = CreateArtifact(
                tempRoot,
                "candidate",
                170,
                30,
                12,
                10,
                "visible_stable",
                "none");

            var referenceDirs = new[]
            {
                Path.Combine(tempRoot, "ref-a"),
                Path.Combine(tempRoot, "ref-b"),
                Path.Combine(tempRoot, "ref-c"),
            };

            foreach (var referenceDir in referenceDirs)
            {
                Directory.CreateDirectory(referenceDir);
            }

            var result = await RunScriptAsync(candidate, referenceDirs);
            Assert.True(
                result.ExitCode == 0,
                $"ExitCode={result.ExitCode}{Environment.NewLine}STDOUT:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}STDERR:{Environment.NewLine}{result.StandardError}");
            Assert.Contains("classification=receive_arrival_latency", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("reference_ready_path_comparison_mode=candidate_stage_composition_fallback", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task AnalyzeHelperReadyPath_ParsesRedactedSummaryLinesWhenHeaderValuesAreMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-ready-path-analysis", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var candidate = CreateRedactedArtifact(
                tempRoot,
                "candidate",
                290,
                4,
                0,
                0,
                "visible_stable",
                "none");

            var referenceDirs = new[]
            {
                Path.Combine(tempRoot, "ref-a"),
                Path.Combine(tempRoot, "ref-b"),
                Path.Combine(tempRoot, "ref-c"),
            };

            foreach (var referenceDir in referenceDirs)
            {
                Directory.CreateDirectory(referenceDir);
            }

            var result = await RunScriptAsync(candidate, referenceDirs);
            Assert.True(
                result.ExitCode == 0,
                $"ExitCode={result.ExitCode}{Environment.NewLine}STDOUT:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}STDERR:{Environment.NewLine}{result.StandardError}");
            Assert.Contains("classification=receive_arrival_latency", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("candidate_capture_to_first_fragment_observed_median_ms=290", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static async Task RunClassificationCaseAsync(
        int candidateCaptureToFirstFragmentObservedMedianMs,
        int candidateFirstFragmentToLastFragmentObservedMedianMs,
        int candidateLastFragmentToAssemblyCompleteMedianMs,
        int candidateAssemblyCompleteToFrameEmittedMedianMs,
        string expectedClassification)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-ready-path-analysis", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var references = new[]
            {
                CreateArtifact(tempRoot, "ref-a", 40, 15, 10, 8, "visible_stable", "none"),
                CreateArtifact(tempRoot, "ref-b", 42, 16, 12, 9, "visible_stable", "none"),
                CreateArtifact(tempRoot, "ref-c", 45, 18, 13, 10, "visible_stable", "none"),
            };

            var candidate = CreateArtifact(
                tempRoot,
                "candidate",
                candidateCaptureToFirstFragmentObservedMedianMs,
                candidateFirstFragmentToLastFragmentObservedMedianMs,
                candidateLastFragmentToAssemblyCompleteMedianMs,
                candidateAssemblyCompleteToFrameEmittedMedianMs,
                "visible_stable",
                "none");

            var result = await RunScriptAsync(candidate, references);
            Assert.True(
                result.ExitCode == 0,
                $"ExitCode={result.ExitCode}{Environment.NewLine}STDOUT:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}STDERR:{Environment.NewLine}{result.StandardError}");
            Assert.Contains($"classification={expectedClassification}", result.StandardOutput, StringComparison.Ordinal);

            var reportPath = Path.Combine(candidate, "helper-ready-path-analysis.txt");
            Assert.True(File.Exists(reportPath));
            var report = File.ReadAllText(reportPath);
            Assert.Contains($"classification={expectedClassification}", report, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunScriptAsync(
        string candidateArtifactDir,
        IReadOnlyCollection<string> referenceArtifactDirs)
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var scriptPath = Path.Combine(repoRoot, "tools", "Analyze-ScreenShareHelperReadyPath.ps1");
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
        process.StartInfo.ArgumentList.Add("-ReferenceArtifactDirs");
        process.StartInfo.ArgumentList.Add(string.Join(",", referenceArtifactDirs));

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string CreateArtifact(
        string root,
        string name,
        int captureToFirstFragmentObservedMedianMs,
        int firstFragmentToLastFragmentObservedMedianMs,
        int lastFragmentToAssemblyCompleteMedianMs,
        int assemblyCompleteToFrameEmittedMedianMs,
        string helperSessionPhase,
        string helperRecoveryMechanism)
    {
        var artifactDir = Path.Combine(root, name);
        Directory.CreateDirectory(artifactDir);

        var lines = new[]
        {
            "log_path=synthetic.log",
            $"capture_to_first_fragment_observed_avg_ms={captureToFirstFragmentObservedMedianMs}",
            $"capture_to_first_fragment_observed_median_ms={captureToFirstFragmentObservedMedianMs}",
            $"capture_to_first_fragment_observed_p95_ms={captureToFirstFragmentObservedMedianMs}",
            $"capture_to_first_fragment_observed_max_ms={captureToFirstFragmentObservedMedianMs}",
            $"first_fragment_to_last_fragment_observed_avg_ms={firstFragmentToLastFragmentObservedMedianMs}",
            $"first_fragment_to_last_fragment_observed_median_ms={firstFragmentToLastFragmentObservedMedianMs}",
            $"first_fragment_to_last_fragment_observed_p95_ms={firstFragmentToLastFragmentObservedMedianMs}",
            $"first_fragment_to_last_fragment_observed_max_ms={firstFragmentToLastFragmentObservedMedianMs}",
            $"last_fragment_to_assembly_complete_avg_ms={lastFragmentToAssemblyCompleteMedianMs}",
            $"last_fragment_to_assembly_complete_median_ms={lastFragmentToAssemblyCompleteMedianMs}",
            $"last_fragment_to_assembly_complete_p95_ms={lastFragmentToAssemblyCompleteMedianMs}",
            $"last_fragment_to_assembly_complete_max_ms={lastFragmentToAssemblyCompleteMedianMs}",
            $"assembly_complete_to_frame_emitted_avg_ms={assemblyCompleteToFrameEmittedMedianMs}",
            $"assembly_complete_to_frame_emitted_median_ms={assemblyCompleteToFrameEmittedMedianMs}",
            $"assembly_complete_to_frame_emitted_p95_ms={assemblyCompleteToFrameEmittedMedianMs}",
            $"assembly_complete_to_frame_emitted_max_ms={assemblyCompleteToFrameEmittedMedianMs}",
            "dominant_ready_path_stage=none",
            $"helper_session_phase={helperSessionPhase}",
            $"helper_recovery_mechanism={helperRecoveryMechanism}",
            "",
            "helper_ready_path_summary_lines:",
            $"event=screenshare_helper_ready_path_summary; role=helper_remote; trigger=periodic; session_id=synth; helper_session_phase={helperSessionPhase}; helper_recovery_mechanism={helperRecoveryMechanism}; capture_to_first_fragment_observed_median_ms={captureToFirstFragmentObservedMedianMs}; first_fragment_to_last_fragment_observed_median_ms={firstFragmentToLastFragmentObservedMedianMs}; last_fragment_to_assembly_complete_median_ms={lastFragmentToAssemblyCompleteMedianMs}; assembly_complete_to_frame_emitted_median_ms={assemblyCompleteToFrameEmittedMedianMs}; dominant_ready_path_stage=none"
        };

        File.WriteAllLines(Path.Combine(artifactDir, "helper-ready-path-summary.txt"), lines);
        return artifactDir;
    }

    private static string CreateRedactedArtifact(
        string root,
        string name,
        int captureToFirstFragmentObservedMedianMs,
        int firstFragmentToLastFragmentObservedMedianMs,
        int lastFragmentToAssemblyCompleteMedianMs,
        int assemblyCompleteToFrameEmittedMedianMs,
        string helperSessionPhase,
        string helperRecoveryMechanism)
    {
        var artifactDir = Path.Combine(root, name);
        Directory.CreateDirectory(artifactDir);

        var lines = new[]
        {
            "log_path=synthetic.log",
            "capture_to_first_fragment_observed_avg_ms=-1",
            "capture_to_first_fragment_observed_median_ms=-1",
            "capture_to_first_fragment_observed_p95_ms=-1",
            "capture_to_first_fragment_observed_max_ms=-1",
            "first_fragment_to_last_fragment_observed_avg_ms=-1",
            "first_fragment_to_last_fragment_observed_median_ms=-1",
            "first_fragment_to_last_fragment_observed_p95_ms=-1",
            "first_fragment_to_last_fragment_observed_max_ms=-1",
            "last_fragment_to_assembly_complete_avg_ms=-1",
            "last_fragment_to_assembly_complete_median_ms=-1",
            "last_fragment_to_assembly_complete_p95_ms=-1",
            "last_fragment_to_assembly_complete_max_ms=-1",
            "assembly_complete_to_frame_emitted_avg_ms=-1",
            "assembly_complete_to_frame_emitted_median_ms=-1",
            "assembly_complete_to_frame_emitted_p95_ms=-1",
            "assembly_complete_to_frame_emitted_max_ms=-1",
            "dominant_ready_path_stage=capture_to_first_fragment_observed",
            $"helper_session_phase={helperSessionPhase}",
            $"helper_recovery_mechanism={helperRecoveryMechanism}",
            "",
            "helper_ready_path_summary_lines:",
            $"event=screenshare_helper_ready_path_summary; role=helper_remote; trigger=periodic; session_id=synth; helper_session_phase={helperSessionPhase}; helper_recovery_mechanism={helperRecoveryMechanism}; [redacted]={captureToFirstFragmentObservedMedianMs}; [redacted]={captureToFirstFragmentObservedMedianMs}; [redacted]={captureToFirstFragmentObservedMedianMs}; [redacted]={captureToFirstFragmentObservedMedianMs}; [redacted]={firstFragmentToLastFragmentObservedMedianMs}; [redacted]={firstFragmentToLastFragmentObservedMedianMs}; [redacted]={firstFragmentToLastFragmentObservedMedianMs}; [redacted]={firstFragmentToLastFragmentObservedMedianMs}; [redacted]={lastFragmentToAssemblyCompleteMedianMs}; [redacted]={lastFragmentToAssemblyCompleteMedianMs}; [redacted]={lastFragmentToAssemblyCompleteMedianMs}; [redacted]={lastFragmentToAssemblyCompleteMedianMs}; [redacted]={assemblyCompleteToFrameEmittedMedianMs}; [redacted]={assemblyCompleteToFrameEmittedMedianMs}; [redacted]={assemblyCompleteToFrameEmittedMedianMs}; [redacted]={assemblyCompleteToFrameEmittedMedianMs}; dominant_ready_path_stage=capture_to_first_fragment_observed"
        };

        File.WriteAllLines(Path.Combine(artifactDir, "helper-ready-path-summary.txt"), lines);
        return artifactDir;
    }

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
        }
    }
}
