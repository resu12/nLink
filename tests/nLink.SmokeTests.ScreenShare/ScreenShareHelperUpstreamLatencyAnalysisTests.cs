using System.Diagnostics;

namespace NLink.SmokeTests;

[Trait("Area", "ScreenShare")]
[Collection(AvaloniaHeadlessUiCollection.Name)]
public sealed class ScreenShareHelperUpstreamLatencyAnalysisTests
{
    [Fact]
    public async Task AnalyzeHelperUpstreamLatency_ClassifiesPreHelperArrivalLatency()
    {
        await RunClassificationCaseAsync(
            candidateCaptureToFrameReadyMedianMs: 160,
            candidateFrameReadyToViewerAcceptMedianMs: 20,
            candidateViewerAcceptToDecodeEnqueueMedianMs: 10,
            candidateDecodeEnqueueToDecodeStartMedianMs: 12,
            expectedClassification: "pre_helper_arrival_latency");
    }

    [Fact]
    public async Task AnalyzeHelperUpstreamLatency_ClassifiesViewerAdmissionLatency()
    {
        await RunClassificationCaseAsync(
            candidateCaptureToFrameReadyMedianMs: 40,
            candidateFrameReadyToViewerAcceptMedianMs: 120,
            candidateViewerAcceptToDecodeEnqueueMedianMs: 18,
            candidateDecodeEnqueueToDecodeStartMedianMs: 10,
            expectedClassification: "viewer_admission_latency");
    }

    [Fact]
    public async Task AnalyzeHelperUpstreamLatency_ClassifiesDecodeStartLatency()
    {
        await RunClassificationCaseAsync(
            candidateCaptureToFrameReadyMedianMs: 35,
            candidateFrameReadyToViewerAcceptMedianMs: 18,
            candidateViewerAcceptToDecodeEnqueueMedianMs: 9,
            candidateDecodeEnqueueToDecodeStartMedianMs: 140,
            expectedClassification: "decode_start_latency");
    }

    [Fact]
    public async Task AnalyzeHelperUpstreamLatency_ClassifiesMixedWhenNoDominantStage()
    {
        await RunClassificationCaseAsync(
            candidateCaptureToFrameReadyMedianMs: 70,
            candidateFrameReadyToViewerAcceptMedianMs: 60,
            candidateViewerAcceptToDecodeEnqueueMedianMs: 50,
            candidateDecodeEnqueueToDecodeStartMedianMs: 55,
            expectedClassification: "mixed_or_inconclusive");
    }

    [Fact]
    public async Task AnalyzeHelperUpstreamLatency_FallsBackToCandidateCompositionWhenReferenceSummariesAreMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-upstream-latency-analysis", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var candidate = CreateArtifact(
                tempRoot,
                "candidate",
                180,
                15,
                4,
                6,
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
            Assert.Contains("classification=pre_helper_arrival_latency", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("reference_upstream_comparison_mode=candidate_stage_composition_fallback", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static async Task RunClassificationCaseAsync(
        int candidateCaptureToFrameReadyMedianMs,
        int candidateFrameReadyToViewerAcceptMedianMs,
        int candidateViewerAcceptToDecodeEnqueueMedianMs,
        int candidateDecodeEnqueueToDecodeStartMedianMs,
        string expectedClassification)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-upstream-latency-analysis", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var references = new[]
            {
                CreateArtifact(tempRoot, "ref-a", 40, 12, 8, 10, "visible_stable", "none"),
                CreateArtifact(tempRoot, "ref-b", 42, 14, 10, 11, "visible_stable", "none"),
                CreateArtifact(tempRoot, "ref-c", 45, 15, 12, 12, "visible_stable", "none"),
            };

            var candidate = CreateArtifact(
                tempRoot,
                "candidate",
                candidateCaptureToFrameReadyMedianMs,
                candidateFrameReadyToViewerAcceptMedianMs,
                candidateViewerAcceptToDecodeEnqueueMedianMs,
                candidateDecodeEnqueueToDecodeStartMedianMs,
                "visible_stable",
                "none");

            var result = await RunScriptAsync(candidate, references);
            Assert.True(
                result.ExitCode == 0,
                $"ExitCode={result.ExitCode}{Environment.NewLine}STDOUT:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}STDERR:{Environment.NewLine}{result.StandardError}");
            Assert.Contains($"classification={expectedClassification}", result.StandardOutput, StringComparison.Ordinal);

            var reportPath = Path.Combine(candidate, "helper-upstream-latency-analysis.txt");
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
        var scriptPath = Path.Combine(repoRoot, "tools", "Analyze-ScreenShareHelperUpstreamLatency.ps1");
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
        int captureToFrameReadyMedianMs,
        int frameReadyToViewerAcceptMedianMs,
        int viewerAcceptToDecodeEnqueueMedianMs,
        int decodeEnqueueToDecodeStartMedianMs,
        string helperSessionPhase,
        string helperRecoveryMechanism)
    {
        var artifactDir = Path.Combine(root, name);
        Directory.CreateDirectory(artifactDir);
        var captureToDecodeStartMedianMs =
            captureToFrameReadyMedianMs +
            frameReadyToViewerAcceptMedianMs +
            viewerAcceptToDecodeEnqueueMedianMs +
            decodeEnqueueToDecodeStartMedianMs;

        var lines = new[]
        {
            "log_path=synthetic.log",
            $"capture_to_frame_ready_avg_ms={captureToFrameReadyMedianMs}",
            $"capture_to_frame_ready_median_ms={captureToFrameReadyMedianMs}",
            $"capture_to_frame_ready_p95_ms={captureToFrameReadyMedianMs}",
            $"capture_to_frame_ready_max_ms={captureToFrameReadyMedianMs}",
            $"frame_ready_to_viewer_accept_avg_ms={frameReadyToViewerAcceptMedianMs}",
            $"frame_ready_to_viewer_accept_median_ms={frameReadyToViewerAcceptMedianMs}",
            $"frame_ready_to_viewer_accept_p95_ms={frameReadyToViewerAcceptMedianMs}",
            $"frame_ready_to_viewer_accept_max_ms={frameReadyToViewerAcceptMedianMs}",
            $"viewer_accept_to_decode_enqueue_avg_ms={viewerAcceptToDecodeEnqueueMedianMs}",
            $"viewer_accept_to_decode_enqueue_median_ms={viewerAcceptToDecodeEnqueueMedianMs}",
            $"viewer_accept_to_decode_enqueue_p95_ms={viewerAcceptToDecodeEnqueueMedianMs}",
            $"viewer_accept_to_decode_enqueue_max_ms={viewerAcceptToDecodeEnqueueMedianMs}",
            $"decode_enqueue_to_decode_start_avg_ms={decodeEnqueueToDecodeStartMedianMs}",
            $"decode_enqueue_to_decode_start_median_ms={decodeEnqueueToDecodeStartMedianMs}",
            $"decode_enqueue_to_decode_start_p95_ms={decodeEnqueueToDecodeStartMedianMs}",
            $"decode_enqueue_to_decode_start_max_ms={decodeEnqueueToDecodeStartMedianMs}",
            $"capture_to_decode_start_avg_ms={captureToDecodeStartMedianMs}",
            $"capture_to_decode_start_median_ms={captureToDecodeStartMedianMs}",
            $"capture_to_decode_start_p95_ms={captureToDecodeStartMedianMs}",
            $"capture_to_decode_start_max_ms={captureToDecodeStartMedianMs}",
            "worst_epoch_by_capture_to_decode_start=1",
            $"worst_epoch_capture_to_decode_start_avg_ms={captureToDecodeStartMedianMs}",
            "dominant_upstream_latency_stage=none",
            $"helper_session_phase={helperSessionPhase}",
            $"helper_recovery_mechanism={helperRecoveryMechanism}",
            "",
            "helper_upstream_latency_summary_lines:",
            $"event=screenshare_helper_upstream_latency_summary; role=helper_remote; trigger=periodic; session_id=synth; helper_session_phase={helperSessionPhase}; helper_recovery_mechanism={helperRecoveryMechanism}; capture_to_frame_ready_median_ms={captureToFrameReadyMedianMs}; frame_ready_to_viewer_accept_median_ms={frameReadyToViewerAcceptMedianMs}; viewer_accept_to_decode_enqueue_median_ms={viewerAcceptToDecodeEnqueueMedianMs}; decode_enqueue_to_decode_start_median_ms={decodeEnqueueToDecodeStartMedianMs}; capture_to_decode_start_median_ms={captureToDecodeStartMedianMs}"
        };

        File.WriteAllLines(Path.Combine(artifactDir, "helper-upstream-latency-summary.txt"), lines);
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
