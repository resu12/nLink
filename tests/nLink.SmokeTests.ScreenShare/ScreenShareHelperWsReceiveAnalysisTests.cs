using System.Diagnostics;

namespace NLink.SmokeTests;

[Trait("Area", "ScreenShare")]
[Collection(AvaloniaHeadlessUiCollection.Name)]
public sealed class ScreenShareHelperWsReceiveAnalysisTests
{
    [Fact]
    public async Task AnalyzeHelperWsReceive_ClassifiesPreWsReceiverLatency()
    {
        await RunClassificationCaseAsync(180, 12, 8, "pre_ws_receiver_latency");
    }

    [Fact]
    public async Task AnalyzeHelperWsReceive_ClassifiesWsReceiverParseLatency()
    {
        await RunClassificationCaseAsync(14, 170, 10, "ws_receiver_parse_latency");
    }

    [Fact]
    public async Task AnalyzeHelperWsReceive_ClassifiesJsEventListenerLatency()
    {
        await RunClassificationCaseAsync(12, 16, 165, "js_event_listener_latency");
    }

    [Fact]
    public async Task AnalyzeHelperWsReceive_ClassifiesMixedWhenNoDominantStage()
    {
        await RunClassificationCaseAsync(58, 52, 48, "mixed_or_inconclusive");
    }

    [Fact]
    public async Task AnalyzeHelperWsReceive_FallsBackToCandidateCompositionWhenReferenceSummariesAreMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-ws-receive-analysis", Guid.NewGuid().ToString("N"));
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
            Assert.Contains("classification=ws_receiver_parse_latency", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("reference_ws_receive_comparison_mode=candidate_stage_composition_fallback", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static async Task RunClassificationCaseAsync(
        int envelopeSendToWsReceiverWriteEnteredMedianMs,
        int wsReceiverWriteEnteredToWsMessageEmittedMedianMs,
        int wsMessageEmittedToSdkHandleMsgEnteredMedianMs,
        string expectedClassification)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-ws-receive-analysis", Guid.NewGuid().ToString("N"));
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
                envelopeSendToWsReceiverWriteEnteredMedianMs,
                wsReceiverWriteEnteredToWsMessageEmittedMedianMs,
                wsMessageEmittedToSdkHandleMsgEnteredMedianMs,
                "visible_stable",
                "none");

            var result = await RunScriptAsync(candidate, references);
            Assert.True(
                result.ExitCode == 0,
                $"ExitCode={result.ExitCode}{Environment.NewLine}STDOUT:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}STDERR:{Environment.NewLine}{result.StandardError}");
            Assert.Contains($"classification={expectedClassification}", result.StandardOutput, StringComparison.Ordinal);

            var reportPath = Path.Combine(candidate, "helper-ws-receive-analysis.txt");
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
        var scriptPath = Path.Combine(repoRoot, "tools", "Analyze-ScreenShareHelperWsReceive.ps1");
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
        int envelopeSendToWsReceiverWriteEnteredMedianMs,
        int wsReceiverWriteEnteredToWsMessageEmittedMedianMs,
        int wsMessageEmittedToSdkHandleMsgEnteredMedianMs,
        string helperSessionPhase,
        string helperRecoveryMechanism)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);

        var lines = new[]
        {
            "log_path=synth.log",
            $"envelope_send_to_ws_receiver_write_entered_avg_ms={envelopeSendToWsReceiverWriteEnteredMedianMs}",
            $"envelope_send_to_ws_receiver_write_entered_median_ms={envelopeSendToWsReceiverWriteEnteredMedianMs}",
            $"envelope_send_to_ws_receiver_write_entered_p95_ms={envelopeSendToWsReceiverWriteEnteredMedianMs}",
            $"envelope_send_to_ws_receiver_write_entered_max_ms={envelopeSendToWsReceiverWriteEnteredMedianMs}",
            $"ws_receiver_write_entered_to_ws_message_emitted_avg_ms={wsReceiverWriteEnteredToWsMessageEmittedMedianMs}",
            $"ws_receiver_write_entered_to_ws_message_emitted_median_ms={wsReceiverWriteEnteredToWsMessageEmittedMedianMs}",
            $"ws_receiver_write_entered_to_ws_message_emitted_p95_ms={wsReceiverWriteEnteredToWsMessageEmittedMedianMs}",
            $"ws_receiver_write_entered_to_ws_message_emitted_max_ms={wsReceiverWriteEnteredToWsMessageEmittedMedianMs}",
            $"ws_message_emitted_to_sdk_handle_msg_entered_avg_ms={wsMessageEmittedToSdkHandleMsgEnteredMedianMs}",
            $"ws_message_emitted_to_sdk_handle_msg_entered_median_ms={wsMessageEmittedToSdkHandleMsgEnteredMedianMs}",
            $"ws_message_emitted_to_sdk_handle_msg_entered_p95_ms={wsMessageEmittedToSdkHandleMsgEnteredMedianMs}",
            $"ws_message_emitted_to_sdk_handle_msg_entered_max_ms={wsMessageEmittedToSdkHandleMsgEnteredMedianMs}",
            "dominant_ws_receive_stage=none",
            $"helper_session_phase={helperSessionPhase}",
            $"helper_recovery_mechanism={helperRecoveryMechanism}",
            "",
            "helper_ws_receive_summary_lines:",
            $"event=screenshare_helper_ws_receive_summary; role=helper_remote; trigger=periodic; session_id=synth; helper_session_phase={helperSessionPhase}; helper_recovery_mechanism={helperRecoveryMechanism}; envelope_send_to_ws_receiver_write_entered_median_ms={envelopeSendToWsReceiverWriteEnteredMedianMs}; ws_receiver_write_entered_to_ws_message_emitted_median_ms={wsReceiverWriteEnteredToWsMessageEmittedMedianMs}; ws_message_emitted_to_sdk_handle_msg_entered_median_ms={wsMessageEmittedToSdkHandleMsgEnteredMedianMs}; dominant_ws_receive_stage=none"
        };

        File.WriteAllLines(Path.Combine(dir, "helper-ws-receive-summary.txt"), lines);
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
