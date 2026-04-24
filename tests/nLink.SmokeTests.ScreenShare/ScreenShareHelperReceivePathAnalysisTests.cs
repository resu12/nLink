using System.Diagnostics;

namespace NLink.SmokeTests;

[Trait("Area", "ScreenShare")]
[Collection(AvaloniaHeadlessUiCollection.Name)]
public sealed class ScreenShareHelperReceivePathAnalysisTests
{
    [Fact]
    public async Task AnalyzeHelperReceivePath_ClassifiesSenderPreSendLatency()
    {
        await RunClassificationCaseAsync(160, 12, 8, 6, 4, 3, "sender_pre_send_latency");
    }

    [Fact]
    public async Task AnalyzeHelperReceivePath_ClassifiesBridgeReceiveLatency()
    {
        await RunClassificationCaseAsync(14, 180, 10, 7, 5, 4, "bridge_receive_latency");
    }

    [Fact]
    public async Task AnalyzeHelperReceivePath_ClassifiesEnvelopeParseLatency()
    {
        await RunClassificationCaseAsync(12, 14, 170, 8, 4, 3, "envelope_parse_latency");
    }

    [Fact]
    public async Task AnalyzeHelperReceivePath_ClassifiesSecureDecryptLatency()
    {
        await RunClassificationCaseAsync(12, 14, 10, 175, 5, 4, "secure_decrypt_latency");
    }

    [Fact]
    public async Task AnalyzeHelperReceivePath_ClassifiesFragmentDispatchLatency()
    {
        await RunClassificationCaseAsync(12, 14, 10, 8, 155, 22, "fragment_dispatch_latency");
    }

    [Fact]
    public async Task AnalyzeHelperReceivePath_ClassifiesMixedWhenNoDominantStage()
    {
        await RunClassificationCaseAsync(52, 48, 44, 40, 36, 32, "mixed_or_inconclusive");
    }

    [Fact]
    public async Task AnalyzeHelperReceivePath_FallsBackToCandidateCompositionWhenReferenceSummariesAreMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-receive-path-analysis", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var candidate = CreateArtifact(tempRoot, "candidate", 20, 190, 12, 6, 4, 3, "visible_stable", "none");

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
            Assert.Contains("classification=bridge_receive_latency", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("reference_receive_path_comparison_mode=candidate_stage_composition_fallback", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static async Task RunClassificationCaseAsync(
        int captureToEnvelopeSendMedianMs,
        int envelopeSendToBridgeIngressMedianMs,
        int bridgeIngressToEnvelopeParsedMedianMs,
        int envelopeParsedToSecureDecryptMedianMs,
        int secureDecryptToFragmentDeserializeMedianMs,
        int fragmentDeserializeToFirstFragmentObservedMedianMs,
        string expectedClassification)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-receive-path-analysis", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var references = new[]
            {
                CreateArtifact(tempRoot, "ref-a", 14, 12, 10, 8, 6, 4, "visible_stable", "none"),
                CreateArtifact(tempRoot, "ref-b", 16, 14, 11, 9, 7, 5, "visible_stable", "none"),
                CreateArtifact(tempRoot, "ref-c", 18, 16, 12, 10, 8, 6, "visible_stable", "none"),
            };

            var candidate = CreateArtifact(
                tempRoot,
                "candidate",
                captureToEnvelopeSendMedianMs,
                envelopeSendToBridgeIngressMedianMs,
                bridgeIngressToEnvelopeParsedMedianMs,
                envelopeParsedToSecureDecryptMedianMs,
                secureDecryptToFragmentDeserializeMedianMs,
                fragmentDeserializeToFirstFragmentObservedMedianMs,
                "visible_stable",
                "none");

            var result = await RunScriptAsync(candidate, references);
            Assert.True(
                result.ExitCode == 0,
                $"ExitCode={result.ExitCode}{Environment.NewLine}STDOUT:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}STDERR:{Environment.NewLine}{result.StandardError}");
            Assert.Contains($"classification={expectedClassification}", result.StandardOutput, StringComparison.Ordinal);

            var reportPath = Path.Combine(candidate, "helper-receive-path-analysis.txt");
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
        var scriptPath = Path.Combine(repoRoot, "tools", "Analyze-ScreenShareHelperReceivePath.ps1");
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
        int captureToEnvelopeSendMedianMs,
        int envelopeSendToBridgeIngressMedianMs,
        int bridgeIngressToEnvelopeParsedMedianMs,
        int envelopeParsedToSecureDecryptMedianMs,
        int secureDecryptToFragmentDeserializeMedianMs,
        int fragmentDeserializeToFirstFragmentObservedMedianMs,
        string helperSessionPhase,
        string helperRecoveryMechanism)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);

        var lines = new[]
        {
            "log_path=synth.log",
            $"capture_to_envelope_send_avg_ms={captureToEnvelopeSendMedianMs}",
            $"capture_to_envelope_send_median_ms={captureToEnvelopeSendMedianMs}",
            $"capture_to_envelope_send_p95_ms={captureToEnvelopeSendMedianMs}",
            $"capture_to_envelope_send_max_ms={captureToEnvelopeSendMedianMs}",
            $"envelope_send_to_bridge_ingress_avg_ms={envelopeSendToBridgeIngressMedianMs}",
            $"envelope_send_to_bridge_ingress_median_ms={envelopeSendToBridgeIngressMedianMs}",
            $"envelope_send_to_bridge_ingress_p95_ms={envelopeSendToBridgeIngressMedianMs}",
            $"envelope_send_to_bridge_ingress_max_ms={envelopeSendToBridgeIngressMedianMs}",
            $"bridge_ingress_to_envelope_parsed_avg_ms={bridgeIngressToEnvelopeParsedMedianMs}",
            $"bridge_ingress_to_envelope_parsed_median_ms={bridgeIngressToEnvelopeParsedMedianMs}",
            $"bridge_ingress_to_envelope_parsed_p95_ms={bridgeIngressToEnvelopeParsedMedianMs}",
            $"bridge_ingress_to_envelope_parsed_max_ms={bridgeIngressToEnvelopeParsedMedianMs}",
            $"envelope_parsed_to_secure_decrypt_avg_ms={envelopeParsedToSecureDecryptMedianMs}",
            $"envelope_parsed_to_secure_decrypt_median_ms={envelopeParsedToSecureDecryptMedianMs}",
            $"envelope_parsed_to_secure_decrypt_p95_ms={envelopeParsedToSecureDecryptMedianMs}",
            $"envelope_parsed_to_secure_decrypt_max_ms={envelopeParsedToSecureDecryptMedianMs}",
            $"secure_decrypt_to_fragment_deserialize_avg_ms={secureDecryptToFragmentDeserializeMedianMs}",
            $"secure_decrypt_to_fragment_deserialize_median_ms={secureDecryptToFragmentDeserializeMedianMs}",
            $"secure_decrypt_to_fragment_deserialize_p95_ms={secureDecryptToFragmentDeserializeMedianMs}",
            $"secure_decrypt_to_fragment_deserialize_max_ms={secureDecryptToFragmentDeserializeMedianMs}",
            $"fragment_deserialize_to_first_fragment_observed_avg_ms={fragmentDeserializeToFirstFragmentObservedMedianMs}",
            $"fragment_deserialize_to_first_fragment_observed_median_ms={fragmentDeserializeToFirstFragmentObservedMedianMs}",
            $"fragment_deserialize_to_first_fragment_observed_p95_ms={fragmentDeserializeToFirstFragmentObservedMedianMs}",
            $"fragment_deserialize_to_first_fragment_observed_max_ms={fragmentDeserializeToFirstFragmentObservedMedianMs}",
            "dominant_receive_path_stage=none",
            $"helper_session_phase={helperSessionPhase}",
            $"helper_recovery_mechanism={helperRecoveryMechanism}",
            "",
            "helper_receive_path_summary_lines:",
            $"event=screenshare_helper_receive_path_summary; role=helper_remote; trigger=periodic; session_id=synth; helper_session_phase={helperSessionPhase}; helper_recovery_mechanism={helperRecoveryMechanism}; capture_to_envelope_send_median_ms={captureToEnvelopeSendMedianMs}; envelope_send_to_bridge_ingress_median_ms={envelopeSendToBridgeIngressMedianMs}; bridge_ingress_to_envelope_parsed_median_ms={bridgeIngressToEnvelopeParsedMedianMs}; envelope_parsed_to_secure_decrypt_median_ms={envelopeParsedToSecureDecryptMedianMs}; secure_decrypt_to_fragment_deserialize_median_ms={secureDecryptToFragmentDeserializeMedianMs}; fragment_deserialize_to_first_fragment_observed_median_ms={fragmentDeserializeToFirstFragmentObservedMedianMs}; dominant_receive_path_stage=none"
        };

        File.WriteAllLines(Path.Combine(dir, "helper-receive-path-summary.txt"), lines);
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
