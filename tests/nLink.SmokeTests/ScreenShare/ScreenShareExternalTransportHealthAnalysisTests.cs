using System.Diagnostics;

namespace NLink.SmokeTests;

[Trait("Area", "ScreenShare")]
public sealed class ScreenShareExternalTransportHealthAnalysisTests
{
    [Fact]
    public async Task AnalyzeExternalTransportHealth_ClassifiesRpcSelectionChurnLatency()
    {
        await RunClassificationCaseAsync(
            "rpc_selection_churn_latency",
            new[]
            {
                new TransportWindowSpec(0, 180, 420, "rpc-a", "initial", 1, 9000, 0, 0, 0, 1, 5),
                new TransportWindowSpec(2, 190, 430, "rpc-b", "fallback", 1, 9200, 0, 0, 0, 1, 4),
                new TransportWindowSpec(4, 175, 405, "rpc-c", "fallback", 1, 9100, 0, 0, 0, 1, 3),
            });
    }

    [Fact]
    public async Task AnalyzeExternalTransportHealth_ClassifiesDisconnectRecoveryChurnLatency()
    {
        await RunClassificationCaseAsync(
            "disconnect_recovery_churn_latency",
            new[]
            {
                new TransportWindowSpec(0, 180, 420, "rpc-a", "initial", 0, 1200, 1, 0, 0, 0, 5),
                new TransportWindowSpec(2, 170, 410, "rpc-a", "initial", 0, 800, 1, 0, 0, 0, 4),
                new TransportWindowSpec(4, 165, 405, "rpc-a", "initial", 1, 2500, 0, 0, 0, 0, 3),
            });
    }

    [Fact]
    public async Task AnalyzeExternalTransportHealth_ClassifiesBridgeTransportHealthBurstLatency()
    {
        await RunClassificationCaseAsync(
            "bridge_transport_health_burst_latency",
            new[]
            {
                new TransportWindowSpec(0, 180, 420, "rpc-a", "initial", 1, 9000, 0, 1, 1, 0, 5),
                new TransportWindowSpec(2, 170, 405, "rpc-a", "initial", 1, 9200, 0, 1, 0, 0, 4),
                new TransportWindowSpec(4, 165, 401, "rpc-a", "initial", 1, 9300, 0, 0, 1, 0, 3),
            });
    }

    [Fact]
    public async Task AnalyzeExternalTransportHealth_ClassifiesSteadyExternalDeliveryLatency()
    {
        await RunClassificationCaseAsync(
            "steady_external_delivery_latency",
            new[]
            {
                new TransportWindowSpec(0, 180, 420, "rpc-a", "initial", 1, 9000, 0, 0, 0, 0, 5),
                new TransportWindowSpec(2, 175, 410, "rpc-a", "initial", 1, 9200, 0, 0, 0, 0, 4),
                new TransportWindowSpec(4, 170, 405, "rpc-a", "initial", 1, 9400, 0, 0, 0, 0, 3),
            });
    }

    [Fact]
    public async Task AnalyzeExternalTransportHealth_ClassifiesMixedWhenNoDominantTransportMarker()
    {
        await RunClassificationCaseAsync(
            "mixed_or_inconclusive",
            new[]
            {
                new TransportWindowSpec(0, 180, 420, "rpc-a", "fallback", 1, 9000, 0, 0, 0, 1, 5),
                new TransportWindowSpec(2, 170, 410, "rpc-a", "initial", 0, 1200, 1, 0, 0, 0, 4),
                new TransportWindowSpec(4, 165, 405, "rpc-a", "initial", 1, 9200, 0, 1, 0, 0, 3),
                new TransportWindowSpec(6, 160, 401, "rpc-a", "initial", 1, 9500, 0, 0, 0, 0, 2),
            });
    }

    [Fact]
    public async Task AnalyzeExternalTransportHealth_FallsBackToAllWindowsWhenNoSenderActiveWindowsExist()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-external-transport-health-analysis", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var candidate = CreateArtifact(
                tempRoot,
                "candidate",
                new[]
                {
                    new SocketWindowSpec(0, 180, 420),
                    new SocketWindowSpec(2, 170, 405),
                },
                new[]
                {
                    new TransportWindowSpec(0, 180, 420, "rpc-a", "initial", 1, 9000, 0, 0, 0, 0, 0),
                    new TransportWindowSpec(2, 170, 405, "rpc-a", "initial", 1, 9200, 0, 0, 0, 0, 0),
                });

            var result = await RunScriptAsync(candidate);
            Assert.True(
                result.ExitCode == 0,
                $"ExitCode={result.ExitCode}{Environment.NewLine}STDOUT:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}STDERR:{Environment.NewLine}{result.StandardError}");
            Assert.Contains("classification=steady_external_delivery_latency", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("sender_active_window_mode=fallback_all_windows", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("analysis_mode=candidate_window_correlation_only", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static async Task RunClassificationCaseAsync(
        string expectedClassification,
        IReadOnlyCollection<TransportWindowSpec> transportWindows)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-external-transport-health-analysis", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var socketWindows = transportWindows
                .Select(window => new SocketWindowSpec(window.OffsetSeconds, window.MedianMs, window.P95Ms))
                .ToArray();

            var candidate = CreateArtifact(tempRoot, "candidate", socketWindows, transportWindows.ToArray());
            var result = await RunScriptAsync(candidate);
            Assert.True(
                result.ExitCode == 0,
                $"ExitCode={result.ExitCode}{Environment.NewLine}STDOUT:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}STDERR:{Environment.NewLine}{result.StandardError}");
            Assert.Contains($"classification={expectedClassification}", result.StandardOutput, StringComparison.Ordinal);

            var reportPath = Path.Combine(candidate, "helper-external-transport-health-analysis.txt");
            Assert.True(File.Exists(reportPath));
            var report = File.ReadAllText(reportPath);
            Assert.Contains($"classification={expectedClassification}", report, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunScriptAsync(string candidateArtifactDir)
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var scriptPath = Path.Combine(repoRoot, "tools", "Analyze-ScreenShareExternalTransportHealth.ps1");
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

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string CreateArtifact(
        string root,
        string name,
        IReadOnlyCollection<SocketWindowSpec> socketWindows,
        IReadOnlyCollection<TransportWindowSpec> transportWindows)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);

        var baseTimestamp = DateTimeOffset.Parse("2026-04-23T16:17:20Z");
        var latestTransportWindow = transportWindows.Last();
        var uniqueRpcCount = transportWindows
            .Select(window => window.SelectedRpcKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var helperSocketSummaryLines = new List<string>
        {
            "log_path=synth.log",
            $"envelope_send_to_socket_data_event_emitted_avg_ms={socketWindows.Last().MedianMs}",
            $"envelope_send_to_socket_data_event_emitted_median_ms={socketWindows.Last().MedianMs}",
            $"envelope_send_to_socket_data_event_emitted_p95_ms={socketWindows.Last().P95Ms}",
            $"envelope_send_to_socket_data_event_emitted_max_ms={Math.Max(socketWindows.Last().P95Ms, socketWindows.Last().MedianMs)}",
            "socket_data_event_emitted_to_ws_receiver_write_entered_avg_ms=0",
            "socket_data_event_emitted_to_ws_receiver_write_entered_median_ms=0",
            "socket_data_event_emitted_to_ws_receiver_write_entered_p95_ms=0",
            "socket_data_event_emitted_to_ws_receiver_write_entered_max_ms=0",
            "dominant_socket_receive_stage=external_receive_latency",
            "helper_session_phase=visible_stable",
            "helper_recovery_mechanism=none",
            string.Empty,
            "helper_socket_receive_summary_lines:"
        };

        helperSocketSummaryLines.AddRange(socketWindows.Select(window =>
        {
            var timestamp = baseTimestamp.AddSeconds(window.OffsetSeconds).ToString("yyyy-MM-dd HH:mm:ssZ");
            return $"[{timestamp}] [INFO] [ScreenShare] event=screenshare_helper_socket_receive_summary; role=helper_remote; trigger=periodic; session_id=synth; helper_session_phase=visible_stable; helper_recovery_mechanism=none; envelope_send_to_socket_data_event_emitted_median_ms={window.MedianMs}; envelope_send_to_socket_data_event_emitted_p95_ms={window.P95Ms}; dominant_socket_receive_stage=external_receive_latency";
        }));

        var transportHealthSummaryLines = new List<string>
        {
            "log_path=synth.log",
            $"selected_rpc={latestTransportWindow.SelectedRpcKey}",
            $"selected_rpc_key={latestTransportWindow.SelectedRpcKey}",
            $"selected_rpc_stage={latestTransportWindow.SelectedRpcStage}",
            "connect_id=conn-synth",
            "connect_key=conn-synth",
            $"ready_emitted={latestTransportWindow.ReadyEmitted}",
            $"client_ready_age_ms={latestTransportWindow.ClientReadyAgeMs}",
            $"disconnect_count_since_last={latestTransportWindow.DisconnectCountSinceLast}",
            $"connect_failed_count_since_last={latestTransportWindow.ConnectFailedCountSinceLast}",
            $"ws_error_count_since_last={latestTransportWindow.WsErrorCountSinceLast}",
            $"rpc_fallback_attempt_count_since_last={latestTransportWindow.RpcFallbackAttemptCountSinceLast}",
            "control_ready=1",
            "media_ready=1",
            "bulk_ready=1",
            $"frames_sent_since_last={latestTransportWindow.FramesSentSinceLast}",
            "latest_disconnect_reason=(none)",
            "sample_window_ms=2000",
            $"unique_selected_rpc_count={uniqueRpcCount}",
            string.Empty,
            "bridge_transport_health_summary_lines:"
        };

        transportHealthSummaryLines.AddRange(transportWindows.Select(window =>
        {
            var timestamp = baseTimestamp.AddSeconds(window.OffsetSeconds).ToString("yyyy-MM-dd HH:mm:ssZ");
            return $"[{timestamp}] [INFO] [NKN.Bridge] event=screenshare_bridge_transport_health_summary; srk={window.SelectedRpcKey}; srs={window.SelectedRpcStage}; cky=conn-synth; rdy={window.ReadyEmitted}; cra={window.ClientReadyAgeMs}; dcc={window.DisconnectCountSinceLast}; cfc={window.ConnectFailedCountSinceLast}; wec={window.WsErrorCountSinceLast}; rfc={window.RpcFallbackAttemptCountSinceLast}; cr=1; mr=1; br=1; fss={window.FramesSentSinceLast}; ldr=(none); sample_window_ms=2000";
        }));

        File.WriteAllLines(Path.Combine(dir, "helper-socket-receive-summary.txt"), helperSocketSummaryLines);
        File.WriteAllLines(Path.Combine(dir, "bridge-transport-health-summary.txt"), transportHealthSummaryLines);
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

    private sealed record SocketWindowSpec(int OffsetSeconds, int MedianMs, int P95Ms);

    private sealed record TransportWindowSpec(
        int OffsetSeconds,
        int MedianMs,
        int P95Ms,
        string SelectedRpcKey,
        string SelectedRpcStage,
        int ReadyEmitted,
        int ClientReadyAgeMs,
        int DisconnectCountSinceLast,
        int ConnectFailedCountSinceLast,
        int WsErrorCountSinceLast,
        int RpcFallbackAttemptCountSinceLast,
        int FramesSentSinceLast);
}
