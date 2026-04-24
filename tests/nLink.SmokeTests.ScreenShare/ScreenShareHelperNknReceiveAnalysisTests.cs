using System.Diagnostics;

namespace NLink.SmokeTests;

[Trait("Area", "ScreenShare")]
[Collection(AvaloniaHeadlessUiCollection.Name)]
public sealed class ScreenShareHelperNknReceiveAnalysisTests
{
    [Fact]
    public async Task AnalyzeHelperNknReceive_ClassifiesPreSdkHandleLatency()
    {
        await RunClassificationCaseAsync(180, 12, 8, 6, "pre_sdk_handle_latency");
    }

    [Fact]
    public async Task AnalyzeHelperNknReceive_ClassifiesSdkClientInboundProcessingLatency()
    {
        await RunClassificationCaseAsync(14, 170, 10, 8, "sdk_client_inbound_processing_latency");
    }

    [Fact]
    public async Task AnalyzeHelperNknReceive_ClassifiesSdkMulticlientFaninLatency()
    {
        await RunClassificationCaseAsync(14, 18, 165, 10, "sdk_multiclient_fanin_latency");
    }

    [Fact]
    public async Task AnalyzeHelperNknReceive_ClassifiesBridgeListenerEntryLatency()
    {
        await RunClassificationCaseAsync(12, 14, 18, 160, "bridge_listener_entry_latency");
    }

    [Fact]
    public async Task AnalyzeHelperNknReceive_ClassifiesMixedWhenNoDominantStage()
    {
        await RunClassificationCaseAsync(56, 52, 48, 44, "mixed_or_inconclusive");
    }

    [Fact]
    public async Task AnalyzeHelperNknReceive_FallsBackToCandidateCompositionWhenReferenceSummariesAreMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-nkn-receive-analysis", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var candidate = CreateArtifact(tempRoot, "candidate", 20, 190, 12, 10, "visible_stable", "none");
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
            Assert.Contains("classification=sdk_client_inbound_processing_latency", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("reference_nkn_receive_comparison_mode=candidate_stage_composition_fallback", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static async Task RunClassificationCaseAsync(
        int envelopeSendToSdkHandleMsgEnteredMedianMs,
        int sdkHandleMsgEnteredToClientMessageDispatchMedianMs,
        int clientMessageDispatchToMultiClientMessageDispatchMedianMs,
        int multiClientMessageDispatchToBridgeMessageObservedMedianMs,
        string expectedClassification)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-nkn-receive-analysis", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var references = new[]
            {
                CreateArtifact(tempRoot, "ref-a", 14, 12, 10, 8, "visible_stable", "none"),
                CreateArtifact(tempRoot, "ref-b", 16, 14, 11, 9, "visible_stable", "none"),
                CreateArtifact(tempRoot, "ref-c", 18, 16, 12, 10, "visible_stable", "none"),
            };

            var candidate = CreateArtifact(
                tempRoot,
                "candidate",
                envelopeSendToSdkHandleMsgEnteredMedianMs,
                sdkHandleMsgEnteredToClientMessageDispatchMedianMs,
                clientMessageDispatchToMultiClientMessageDispatchMedianMs,
                multiClientMessageDispatchToBridgeMessageObservedMedianMs,
                "visible_stable",
                "none");

            var result = await RunScriptAsync(candidate, references);
            Assert.True(
                result.ExitCode == 0,
                $"ExitCode={result.ExitCode}{Environment.NewLine}STDOUT:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}STDERR:{Environment.NewLine}{result.StandardError}");
            Assert.Contains($"classification={expectedClassification}", result.StandardOutput, StringComparison.Ordinal);

            var reportPath = Path.Combine(candidate, "helper-nkn-receive-analysis.txt");
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
        var scriptPath = Path.Combine(repoRoot, "tools", "Analyze-ScreenShareHelperNknReceive.ps1");
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
        int envelopeSendToSdkHandleMsgEnteredMedianMs,
        int sdkHandleMsgEnteredToClientMessageDispatchMedianMs,
        int clientMessageDispatchToMultiClientMessageDispatchMedianMs,
        int multiClientMessageDispatchToBridgeMessageObservedMedianMs,
        string helperSessionPhase,
        string helperRecoveryMechanism)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);

        var lines = new[]
        {
            "log_path=synth.log",
            $"envelope_send_to_sdk_handle_msg_entered_avg_ms={envelopeSendToSdkHandleMsgEnteredMedianMs}",
            $"envelope_send_to_sdk_handle_msg_entered_median_ms={envelopeSendToSdkHandleMsgEnteredMedianMs}",
            $"envelope_send_to_sdk_handle_msg_entered_p95_ms={envelopeSendToSdkHandleMsgEnteredMedianMs}",
            $"envelope_send_to_sdk_handle_msg_entered_max_ms={envelopeSendToSdkHandleMsgEnteredMedianMs}",
            $"sdk_handle_msg_entered_to_client_message_dispatch_avg_ms={sdkHandleMsgEnteredToClientMessageDispatchMedianMs}",
            $"sdk_handle_msg_entered_to_client_message_dispatch_median_ms={sdkHandleMsgEnteredToClientMessageDispatchMedianMs}",
            $"sdk_handle_msg_entered_to_client_message_dispatch_p95_ms={sdkHandleMsgEnteredToClientMessageDispatchMedianMs}",
            $"sdk_handle_msg_entered_to_client_message_dispatch_max_ms={sdkHandleMsgEnteredToClientMessageDispatchMedianMs}",
            $"client_message_dispatch_to_multiclient_message_dispatch_avg_ms={clientMessageDispatchToMultiClientMessageDispatchMedianMs}",
            $"client_message_dispatch_to_multiclient_message_dispatch_median_ms={clientMessageDispatchToMultiClientMessageDispatchMedianMs}",
            $"client_message_dispatch_to_multiclient_message_dispatch_p95_ms={clientMessageDispatchToMultiClientMessageDispatchMedianMs}",
            $"client_message_dispatch_to_multiclient_message_dispatch_max_ms={clientMessageDispatchToMultiClientMessageDispatchMedianMs}",
            $"multiclient_message_dispatch_to_bridge_message_observed_avg_ms={multiClientMessageDispatchToBridgeMessageObservedMedianMs}",
            $"multiclient_message_dispatch_to_bridge_message_observed_median_ms={multiClientMessageDispatchToBridgeMessageObservedMedianMs}",
            $"multiclient_message_dispatch_to_bridge_message_observed_p95_ms={multiClientMessageDispatchToBridgeMessageObservedMedianMs}",
            $"multiclient_message_dispatch_to_bridge_message_observed_max_ms={multiClientMessageDispatchToBridgeMessageObservedMedianMs}",
            "dominant_nkn_receive_stage=none",
            $"helper_session_phase={helperSessionPhase}",
            $"helper_recovery_mechanism={helperRecoveryMechanism}",
            "",
            "helper_nkn_receive_summary_lines:",
            $"event=screenshare_helper_nkn_receive_summary; role=helper_remote; trigger=periodic; session_id=synth; helper_session_phase={helperSessionPhase}; helper_recovery_mechanism={helperRecoveryMechanism}; envelope_send_to_sdk_handle_msg_entered_median_ms={envelopeSendToSdkHandleMsgEnteredMedianMs}; sdk_handle_msg_entered_to_client_message_dispatch_median_ms={sdkHandleMsgEnteredToClientMessageDispatchMedianMs}; client_message_dispatch_to_multiclient_message_dispatch_median_ms={clientMessageDispatchToMultiClientMessageDispatchMedianMs}; multiclient_message_dispatch_to_bridge_message_observed_median_ms={multiClientMessageDispatchToBridgeMessageObservedMedianMs}; dominant_nkn_receive_stage=none"
        };

        File.WriteAllLines(Path.Combine(dir, "helper-nkn-receive-summary.txt"), lines);
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
