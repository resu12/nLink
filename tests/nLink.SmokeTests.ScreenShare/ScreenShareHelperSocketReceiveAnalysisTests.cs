using System.Diagnostics;

namespace NLink.SmokeTests;

[Trait("Area", "ScreenShare")]
[Collection(AvaloniaHeadlessUiCollection.Name)]
public sealed class ScreenShareHelperSocketReceiveAnalysisTests
{
    [Fact]
    public async Task AnalyzeHelperSocketReceive_ClassifiesExternalReceiveLatency()
    {
        await RunClassificationCaseAsync(180, 12, 18, 48, "external_receive_latency");
    }

    [Fact]
    public async Task AnalyzeHelperSocketReceive_ClassifiesNodeEventLoopBacklogLatency()
    {
        await RunClassificationCaseAsync(185, 10, 55, 130, "node_event_loop_backlog_latency");
    }

    [Fact]
    public async Task AnalyzeHelperSocketReceive_ClassifiesSocketToReceiverLatency()
    {
        await RunClassificationCaseAsync(16, 175, 22, 52, "socket_to_receiver_latency");
    }

    [Fact]
    public async Task AnalyzeHelperSocketReceive_ClassifiesMixedWhenNoDominantStage()
    {
        await RunClassificationCaseAsync(56, 52, 18, 40, "mixed_or_inconclusive");
    }

    [Fact]
    public async Task AnalyzeHelperSocketReceive_FallsBackToCandidateCompositionWhenReferenceSummariesAreMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-socket-receive-analysis", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var candidate = CreateArtifact(tempRoot, "candidate", 22, 190, 24, 46, "visible_stable", "none");
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
            Assert.Contains("classification=socket_to_receiver_latency", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("reference_socket_receive_comparison_mode=candidate_stage_composition_fallback", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static async Task RunClassificationCaseAsync(
        int envelopeSendToSocketDataEventEmittedMedianMs,
        int socketDataEventEmittedToWsReceiverWriteEnteredMedianMs,
        int eventLoopP95Ms,
        int eventLoopMaxMs,
        string expectedClassification)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-socket-receive-analysis", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var references = new[]
            {
                CreateArtifact(tempRoot, "ref-a", 14, 12, 14, 42, "visible_stable", "none"),
                CreateArtifact(tempRoot, "ref-b", 16, 14, 16, 48, "visible_stable", "none"),
                CreateArtifact(tempRoot, "ref-c", 18, 16, 18, 54, "visible_stable", "none"),
            };

            var candidate = CreateArtifact(
                tempRoot,
                "candidate",
                envelopeSendToSocketDataEventEmittedMedianMs,
                socketDataEventEmittedToWsReceiverWriteEnteredMedianMs,
                eventLoopP95Ms,
                eventLoopMaxMs,
                "visible_stable",
                "none");

            var result = await RunScriptAsync(candidate, references);
            Assert.True(
                result.ExitCode == 0,
                $"ExitCode={result.ExitCode}{Environment.NewLine}STDOUT:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}STDERR:{Environment.NewLine}{result.StandardError}");
            Assert.Contains($"classification={expectedClassification}", result.StandardOutput, StringComparison.Ordinal);

            var reportPath = Path.Combine(candidate, "helper-socket-receive-analysis.txt");
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
        var scriptPath = Path.Combine(repoRoot, "tools", "Analyze-ScreenShareHelperSocketReceive.ps1");
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
        int envelopeSendToSocketDataEventEmittedMedianMs,
        int socketDataEventEmittedToWsReceiverWriteEnteredMedianMs,
        int eventLoopP95Ms,
        int eventLoopMaxMs,
        string helperSessionPhase,
        string helperRecoveryMechanism)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);

        var socketReceiveLines = new[]
        {
            "log_path=synth.log",
            $"envelope_send_to_socket_data_event_emitted_avg_ms={envelopeSendToSocketDataEventEmittedMedianMs}",
            $"envelope_send_to_socket_data_event_emitted_median_ms={envelopeSendToSocketDataEventEmittedMedianMs}",
            $"envelope_send_to_socket_data_event_emitted_p95_ms={envelopeSendToSocketDataEventEmittedMedianMs}",
            $"envelope_send_to_socket_data_event_emitted_max_ms={envelopeSendToSocketDataEventEmittedMedianMs}",
            $"socket_data_event_emitted_to_ws_receiver_write_entered_avg_ms={socketDataEventEmittedToWsReceiverWriteEnteredMedianMs}",
            $"socket_data_event_emitted_to_ws_receiver_write_entered_median_ms={socketDataEventEmittedToWsReceiverWriteEnteredMedianMs}",
            $"socket_data_event_emitted_to_ws_receiver_write_entered_p95_ms={socketDataEventEmittedToWsReceiverWriteEnteredMedianMs}",
            $"socket_data_event_emitted_to_ws_receiver_write_entered_max_ms={socketDataEventEmittedToWsReceiverWriteEnteredMedianMs}",
            "dominant_socket_receive_stage=none",
            $"helper_session_phase={helperSessionPhase}",
            $"helper_recovery_mechanism={helperRecoveryMechanism}",
            "",
            "helper_socket_receive_summary_lines:",
            $"event=screenshare_helper_socket_receive_summary; role=helper_remote; trigger=periodic; session_id=synth; helper_session_phase={helperSessionPhase}; helper_recovery_mechanism={helperRecoveryMechanism}; envelope_send_to_socket_data_event_emitted_median_ms={envelopeSendToSocketDataEventEmittedMedianMs}; socket_data_event_emitted_to_ws_receiver_write_entered_median_ms={socketDataEventEmittedToWsReceiverWriteEnteredMedianMs}; dominant_socket_receive_stage=none"
        };

        var bridgeEventLoopLines = new[]
        {
            "log_path=synth.log",
            $"event_loop_p95_ms={eventLoopP95Ms}",
            $"event_loop_max_ms={eventLoopMaxMs}",
            $"event_loop_mean_ms={Math.Max(1, Math.Min(eventLoopP95Ms, eventLoopMaxMs))}",
            "sample_window_ms=2000",
            "",
            "bridge_event_loop_summary_lines:",
            $"event=screenshare_bridge_event_loop_summary; event_loop_p95_ms={eventLoopP95Ms}; event_loop_max_ms={eventLoopMaxMs}; event_loop_mean_ms={Math.Max(1, Math.Min(eventLoopP95Ms, eventLoopMaxMs))}; sample_window_ms=2000"
        };

        File.WriteAllLines(Path.Combine(dir, "helper-socket-receive-summary.txt"), socketReceiveLines);
        File.WriteAllLines(Path.Combine(dir, "bridge-event-loop-summary.txt"), bridgeEventLoopLines);
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
