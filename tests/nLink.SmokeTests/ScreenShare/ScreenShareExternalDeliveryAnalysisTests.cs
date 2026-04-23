using System.Diagnostics;

namespace NLink.SmokeTests;

[Trait("Area", "ScreenShare")]
public sealed class ScreenShareExternalDeliveryAnalysisTests
{
    [Fact]
    public async Task AnalyzeExternalDelivery_ClassifiesBridgeSendIngressLatency()
    {
        await RunClassificationCaseAsync(190, 140, 10, 8, 7, "bridge_send_ingress_latency");
    }

    [Fact]
    public async Task AnalyzeExternalDelivery_ClassifiesSenderBridgeQueueLatency()
    {
        await RunClassificationCaseAsync(220, 12, 150, 10, 8, "sender_bridge_queue_latency");
    }

    [Fact]
    public async Task AnalyzeExternalDelivery_ClassifiesSenderBridgePublishLatency()
    {
        await RunClassificationCaseAsync(250, 12, 14, 70, 80, "sender_bridge_publish_latency");
    }

    [Fact]
    public async Task AnalyzeExternalDelivery_ClassifiesNetworkDeliveryLatency()
    {
        await RunClassificationCaseAsync(200, 18, 16, 8, 8, "network_delivery_latency");
    }

    [Fact]
    public async Task AnalyzeExternalDelivery_ClassifiesMixedWhenNoDominantStage()
    {
        await RunClassificationCaseAsync(150, 30, 35, 20, 15, "mixed_or_inconclusive");
    }

    [Fact]
    public async Task AnalyzeExternalDelivery_FallsBackToCandidateCompositionWhenReferenceSummariesAreMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-external-delivery-analysis", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var candidate = CreateArtifact(tempRoot, "candidate", 230, 20, 160, 10, 8, "visible_stable", "none");
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
            Assert.Contains("classification=sender_bridge_queue_latency", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("reference_external_delivery_comparison_mode=candidate_stage_composition_fallback", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static async Task RunClassificationCaseAsync(
        int envelopeSendToSocketDataEventEmittedMedianMs,
        int binarySendFrameObservedToQueueEnqueueMedianMs,
        int queueEnqueueToQueueDequeueMedianMs,
        int queueDequeueToMediaSendStartedMedianMs,
        int mediaSendStartedToMediaSendResolvedMedianMs,
        string expectedClassification)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-external-delivery-analysis", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var references = new[]
            {
                CreateArtifact(tempRoot, "ref-a", 30, 8, 8, 5, 5, "visible_stable", "none"),
                CreateArtifact(tempRoot, "ref-b", 34, 9, 9, 5, 6, "visible_stable", "none"),
                CreateArtifact(tempRoot, "ref-c", 38, 10, 10, 6, 6, "visible_stable", "none"),
            };

            var candidate = CreateArtifact(
                tempRoot,
                "candidate",
                envelopeSendToSocketDataEventEmittedMedianMs,
                binarySendFrameObservedToQueueEnqueueMedianMs,
                queueEnqueueToQueueDequeueMedianMs,
                queueDequeueToMediaSendStartedMedianMs,
                mediaSendStartedToMediaSendResolvedMedianMs,
                "visible_stable",
                "none");

            var result = await RunScriptAsync(candidate, references);
            Assert.True(
                result.ExitCode == 0,
                $"ExitCode={result.ExitCode}{Environment.NewLine}STDOUT:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}STDERR:{Environment.NewLine}{result.StandardError}");
            Assert.Contains($"classification={expectedClassification}", result.StandardOutput, StringComparison.Ordinal);

            var reportPath = Path.Combine(candidate, "helper-external-delivery-analysis.txt");
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
        var scriptPath = Path.Combine(repoRoot, "tools", "Analyze-ScreenShareExternalDelivery.ps1");
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
        int binarySendFrameObservedToQueueEnqueueMedianMs,
        int queueEnqueueToQueueDequeueMedianMs,
        int queueDequeueToMediaSendStartedMedianMs,
        int mediaSendStartedToMediaSendResolvedMedianMs,
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
            "socket_data_event_emitted_to_ws_receiver_write_entered_avg_ms=0",
            "socket_data_event_emitted_to_ws_receiver_write_entered_median_ms=0",
            "socket_data_event_emitted_to_ws_receiver_write_entered_p95_ms=0",
            "socket_data_event_emitted_to_ws_receiver_write_entered_max_ms=0",
            "dominant_socket_receive_stage=none",
            $"helper_session_phase={helperSessionPhase}",
            $"helper_recovery_mechanism={helperRecoveryMechanism}"
        };

        var bridgeMediaSendLines = new[]
        {
            "log_path=synth.log",
            $"binary_send_frame_observed_to_queue_enqueue_avg_ms={binarySendFrameObservedToQueueEnqueueMedianMs}",
            $"binary_send_frame_observed_to_queue_enqueue_median_ms={binarySendFrameObservedToQueueEnqueueMedianMs}",
            $"binary_send_frame_observed_to_queue_enqueue_p95_ms={binarySendFrameObservedToQueueEnqueueMedianMs}",
            $"binary_send_frame_observed_to_queue_enqueue_max_ms={binarySendFrameObservedToQueueEnqueueMedianMs}",
            $"queue_enqueue_to_queue_dequeue_avg_ms={queueEnqueueToQueueDequeueMedianMs}",
            $"queue_enqueue_to_queue_dequeue_median_ms={queueEnqueueToQueueDequeueMedianMs}",
            $"queue_enqueue_to_queue_dequeue_p95_ms={queueEnqueueToQueueDequeueMedianMs}",
            $"queue_enqueue_to_queue_dequeue_max_ms={queueEnqueueToQueueDequeueMedianMs}",
            $"queue_dequeue_to_media_send_started_avg_ms={queueDequeueToMediaSendStartedMedianMs}",
            $"queue_dequeue_to_media_send_started_median_ms={queueDequeueToMediaSendStartedMedianMs}",
            $"queue_dequeue_to_media_send_started_p95_ms={queueDequeueToMediaSendStartedMedianMs}",
            $"queue_dequeue_to_media_send_started_max_ms={queueDequeueToMediaSendStartedMedianMs}",
            $"media_send_started_to_media_send_resolved_avg_ms={mediaSendStartedToMediaSendResolvedMedianMs}",
            $"media_send_started_to_media_send_resolved_median_ms={mediaSendStartedToMediaSendResolvedMedianMs}",
            $"media_send_started_to_media_send_resolved_p95_ms={mediaSendStartedToMediaSendResolvedMedianMs}",
            $"media_send_started_to_media_send_resolved_max_ms={mediaSendStartedToMediaSendResolvedMedianMs}",
            "frames_sent=12",
            "send_failures=0",
            "queue_drops=0",
            "queue_mode=normal",
            "queue_depth=1",
            "oldest_queued_age_ms=4",
            "sample_window_ms=2000",
            "",
            "bridge_media_send_summary_lines:",
            $"event=screenshare_bridge_media_send_summary; binary_send_frame_observed_to_queue_enqueue_median_ms={binarySendFrameObservedToQueueEnqueueMedianMs}; queue_enqueue_to_queue_dequeue_median_ms={queueEnqueueToQueueDequeueMedianMs}; queue_dequeue_to_media_send_started_median_ms={queueDequeueToMediaSendStartedMedianMs}; media_send_started_to_media_send_resolved_median_ms={mediaSendStartedToMediaSendResolvedMedianMs}; frames_sent=12; send_failures=0; queue_drops=0; queue_mode=normal; queue_depth=1; oldest_queued_age_ms=4; sample_window_ms=2000"
        };

        File.WriteAllLines(Path.Combine(dir, "helper-socket-receive-summary.txt"), socketReceiveLines);
        File.WriteAllLines(Path.Combine(dir, "bridge-media-send-summary.txt"), bridgeMediaSendLines);
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
