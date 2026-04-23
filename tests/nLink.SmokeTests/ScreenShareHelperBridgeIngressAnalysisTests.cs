using System.Diagnostics;

namespace NLink.SmokeTests;

public sealed class ScreenShareHelperBridgeIngressAnalysisTests
{
    [Fact]
    public async Task AnalyzeHelperBridgeIngress_ClassifiesUpstreamToBridgeLatency()
    {
        await RunClassificationCaseAsync(180, 12, 8, "upstream_to_bridge_latency");
    }

    [Fact]
    public async Task AnalyzeHelperBridgeIngress_ClassifiesLocalReaderBacklogLatency()
    {
        await RunClassificationCaseAsync(14, 170, 10, "local_reader_backlog_latency");
    }

    [Fact]
    public async Task AnalyzeHelperBridgeIngress_ClassifiesLocalMediaDispatchLatency()
    {
        await RunClassificationCaseAsync(12, 14, 165, "local_media_dispatch_latency");
    }

    [Fact]
    public async Task AnalyzeHelperBridgeIngress_ClassifiesMixedWhenNoDominantStage()
    {
        await RunClassificationCaseAsync(56, 52, 48, "mixed_or_inconclusive");
    }

    [Fact]
    public async Task AnalyzeHelperBridgeIngress_FallsBackToCandidateCompositionWhenReferenceSummariesAreMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-bridge-ingress-analysis", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var candidate = CreateArtifact(tempRoot, "candidate", 20, 190, 12, "visible_stable", "none");
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
            Assert.Contains("classification=local_reader_backlog_latency", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("reference_bridge_ingress_comparison_mode=candidate_stage_composition_fallback", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static async Task RunClassificationCaseAsync(
        int envelopeSendToBridgeMessageObservedMedianMs,
        int bridgeMessageObservedToBinaryFrameDecodedMedianMs,
        int binaryFrameDecodedToBridgeIngressMedianMs,
        string expectedClassification)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-bridge-ingress-analysis", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var references = new[]
            {
                CreateArtifact(tempRoot, "ref-a", 14, 12, 10, "visible_stable", "none"),
                CreateArtifact(tempRoot, "ref-b", 16, 14, 11, "visible_stable", "none"),
                CreateArtifact(tempRoot, "ref-c", 18, 16, 12, "visible_stable", "none"),
            };

            var candidate = CreateArtifact(
                tempRoot,
                "candidate",
                envelopeSendToBridgeMessageObservedMedianMs,
                bridgeMessageObservedToBinaryFrameDecodedMedianMs,
                binaryFrameDecodedToBridgeIngressMedianMs,
                "visible_stable",
                "none");

            var result = await RunScriptAsync(candidate, references);
            Assert.True(
                result.ExitCode == 0,
                $"ExitCode={result.ExitCode}{Environment.NewLine}STDOUT:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}STDERR:{Environment.NewLine}{result.StandardError}");
            Assert.Contains($"classification={expectedClassification}", result.StandardOutput, StringComparison.Ordinal);

            var reportPath = Path.Combine(candidate, "helper-bridge-ingress-analysis.txt");
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
        var scriptPath = Path.Combine(repoRoot, "tools", "Analyze-ScreenShareHelperBridgeIngress.ps1");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            }
        };

        process.StartInfo.ArgumentList.Add("-NoProfile");
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
        int envelopeSendToBridgeMessageObservedMedianMs,
        int bridgeMessageObservedToBinaryFrameDecodedMedianMs,
        int binaryFrameDecodedToBridgeIngressMedianMs,
        string helperSessionPhase,
        string helperRecoveryMechanism)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);

        var lines = new[]
        {
            "log_path=synth.log",
            $"envelope_send_to_bridge_message_observed_avg_ms={envelopeSendToBridgeMessageObservedMedianMs}",
            $"envelope_send_to_bridge_message_observed_median_ms={envelopeSendToBridgeMessageObservedMedianMs}",
            $"envelope_send_to_bridge_message_observed_p95_ms={envelopeSendToBridgeMessageObservedMedianMs}",
            $"envelope_send_to_bridge_message_observed_max_ms={envelopeSendToBridgeMessageObservedMedianMs}",
            $"bridge_message_observed_to_binary_frame_decoded_avg_ms={bridgeMessageObservedToBinaryFrameDecodedMedianMs}",
            $"bridge_message_observed_to_binary_frame_decoded_median_ms={bridgeMessageObservedToBinaryFrameDecodedMedianMs}",
            $"bridge_message_observed_to_binary_frame_decoded_p95_ms={bridgeMessageObservedToBinaryFrameDecodedMedianMs}",
            $"bridge_message_observed_to_binary_frame_decoded_max_ms={bridgeMessageObservedToBinaryFrameDecodedMedianMs}",
            $"binary_frame_decoded_to_bridge_ingress_avg_ms={binaryFrameDecodedToBridgeIngressMedianMs}",
            $"binary_frame_decoded_to_bridge_ingress_median_ms={binaryFrameDecodedToBridgeIngressMedianMs}",
            $"binary_frame_decoded_to_bridge_ingress_p95_ms={binaryFrameDecodedToBridgeIngressMedianMs}",
            $"binary_frame_decoded_to_bridge_ingress_max_ms={binaryFrameDecodedToBridgeIngressMedianMs}",
            "dominant_bridge_ingress_stage=none",
            $"helper_session_phase={helperSessionPhase}",
            $"helper_recovery_mechanism={helperRecoveryMechanism}",
            "",
            "helper_bridge_ingress_summary_lines:",
            $"event=screenshare_helper_bridge_ingress_summary; role=helper_remote; trigger=periodic; session_id=synth; helper_session_phase={helperSessionPhase}; helper_recovery_mechanism={helperRecoveryMechanism}; envelope_send_to_bridge_message_observed_median_ms={envelopeSendToBridgeMessageObservedMedianMs}; bridge_message_observed_to_binary_frame_decoded_median_ms={bridgeMessageObservedToBinaryFrameDecodedMedianMs}; binary_frame_decoded_to_bridge_ingress_median_ms={binaryFrameDecodedToBridgeIngressMedianMs}; dominant_bridge_ingress_stage=none"
        };

        File.WriteAllLines(Path.Combine(dir, "helper-bridge-ingress-summary.txt"), lines);
        return dir;
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
