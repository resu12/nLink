using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class TunaSidecarLiveManualTests
{
    private const string SoakOptInEnv = "NLINK_RUN_TUNA_SOAK_MATRIX";
    private const string SoakDurationMinutesEnv = "NLINK_TUNA_SOAK_DURATION_MIN";
    private const string SoakNetworkLabelEnv = "NLINK_TUNA_SOAK_NETWORK_LABEL";
    private const string SoakNetworkPairIdEnv = "NLINK_TUNA_SOAK_NETWORK_PAIR_ID";
    private const string SoakTiersEnv = "NLINK_TUNA_SOAK_TIERS";
    private const string SoakCellFilterEnv = "NLINK_TUNA_SOAK_CELL_FILTER";
    private const string SoakFilePacingMbpsEnv = "NLINK_TUNA_SOAK_FILE_PACING_MBPS";
    private const string SoakCellRetriesEnv = "NLINK_TUNA_SOAK_CELL_RETRIES";
    private const int SoakDefaultDurationMinutes = 15;
    private const double SoakDefaultFilePacingMbps = 8;
    private const int SoakDefaultCellRetries = 1;
    private const double SoakUnexpectedFallbackRecoveredMinFileRatio = 0.98;

    [Fact]
    public void TunaSoakMatrixOptions_DefaultsAreManualAndTiered()
    {
        var snapshot = CaptureSoakEnvironment();
        try
        {
            ClearSoakEnvironment();

            var options = TunaSoakMatrixOptions.Load();
            var cells = TunaSoakMatrixCell.Build(options);

            Assert.Equal(TimeSpan.FromMinutes(SoakDefaultDurationMinutes), options.CellDuration);
            Assert.Equal("same_machine", options.NetworkLabel);
            Assert.Equal("local", options.NetworkPairId);
            Assert.Equal(SoakDefaultFilePacingMbps, options.FileSendPacingMbps);
            Assert.Equal(SoakDefaultCellRetries, options.MaxCellRetries);
            Assert.Empty(options.CellFilters);
            Assert.Contains(TunaSoakTier.Core, options.Tiers);
            Assert.DoesNotContain(TunaSoakTier.Extended, options.Tiers);
            Assert.Equal(27, cells.Count);
            Assert.All(cells, static cell => Assert.Equal(Phase3TransportMode.Tuna, cell.Transport));
            Assert.Contains(cells, static cell => cell.Payer == TunaSoakPayerMode.HelpeeOnly);
            Assert.Contains(cells, static cell => cell.Payer == TunaSoakPayerMode.HelperOnly);
            Assert.Contains(cells, static cell => cell.Payer == TunaSoakPayerMode.BothUnlocked);
            Assert.Contains(cells, static cell => cell.TrafficProfile == TunaSoakTrafficProfile.ScreenOnly);
            Assert.Contains(cells, static cell => cell.TrafficProfile == TunaSoakTrafficProfile.FileOnly);
            Assert.Contains(cells, static cell => cell.TrafficProfile == TunaSoakTrafficProfile.MixedScreenFile);
            Assert.Contains(cells, static cell => cell.Preset == TunaSoakPreset.TunaQuality);
            Assert.Contains(cells, static cell => cell.Fault == TunaSoakFaultMode.SidecarCrash);
            Assert.Contains(cells, static cell => cell.Fault == TunaSoakFaultMode.SwitchOffFallback);
            Assert.Contains(cells, static cell => cell.Fault == TunaSoakFaultMode.CapReached);
        }
        finally
        {
            RestoreSoakEnvironment(snapshot);
        }
    }

    [Fact]
    public void TunaSoakMatrixOptions_ExtendedAddsProviderTimeout()
    {
        var snapshot = CaptureSoakEnvironment();
        try
        {
            ClearSoakEnvironment();
            Environment.SetEnvironmentVariable(SoakTiersEnv, "core,extended");

            var cells = TunaSoakMatrixCell.Build(TunaSoakMatrixOptions.Load());

            Assert.Contains(cells, static cell => cell.Tier == TunaSoakTier.Extended && cell.Fault == TunaSoakFaultMode.ProviderTimeout);
        }
        finally
        {
            RestoreSoakEnvironment(snapshot);
        }
    }

    [Fact]
    public void TunaSoakMatrixOptions_CellFilterRunsTargetedCells()
    {
        var snapshot = CaptureSoakEnvironment();
        try
        {
            ClearSoakEnvironment();
            Environment.SetEnvironmentVariable(SoakTiersEnv, "core,extended");
            Environment.SetEnvironmentVariable(SoakCellFilterEnv, "core-tuna-mixed-helpee-switch-off,extended-tuna-mixed-helpee-provider-timeout");

            var options = TunaSoakMatrixOptions.Load();
            var cells = TunaSoakMatrixCell.Build(options);

            Assert.Equal(new[] { "core-tuna-mixed-helpee-switch-off", "extended-tuna-mixed-helpee-provider-timeout" }, cells.Select(static cell => cell.CellId).ToArray());
        }
        finally
        {
            RestoreSoakEnvironment(snapshot);
        }
    }

    [Fact]
    public void TunaSoakMatrixArtifactRedaction_RemovesSecretsAndFullPaths()
    {
        const string password = "soak-secret";
        var walletPath = Path.Combine("C:\\Users\\Juraj\\Desktop\\Remote help", "artifacts", "tuna-poc", "wallet-test-nkn.json");
        var input = $"wallet={walletPath}; password={password}; seedBase64=abc; privateKey=def";

        var redacted = RedactPhase3ArtifactText(input, walletPath, password);

        Assert.DoesNotContain(walletPath, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(password, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("seedBase64", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("privateKey", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wallet-test-nkn.json", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void TunaSoakMatrixSummary_RequiresFallbackProofForFaultCells()
    {
        var cell = new TunaSoakMatrixCell(
            TunaSoakTier.Core,
            "core-tuna-high-helpee-crash",
            Phase3TransportMode.Tuna,
            TunaSoakTrafficProfile.MixedScreenFile,
            TunaSoakPreset.HighQuality,
            TunaSoakPayerMode.HelpeeOnly,
            TunaSoakFaultMode.SidecarCrash);
        var result = new TunaSoakCellResult
        {
            CellId = cell.CellId,
            Tier = cell.Tier,
            Transport = cell.Transport,
            TrafficProfile = cell.TrafficProfile,
            Preset = cell.Preset,
            Payer = cell.Payer,
            Fault = cell.Fault,
            Completed = true,
            SessionAlive = true,
            ChatControlAlive = true,
            FileCompleted = true,
            ScreenCompleted = true,
            FallbackExpected = true,
            FallbackStarted = true,
            FallbackFileSent = false,
            FallbackFileReceived = true,
            FallbackScreenSent = true,
            FallbackScreenReceived = true,
        };

        var summary = TunaSoakMatrixSummary.Build(new[] { result });

        Assert.Equal("fail", summary.Verdict);
        Assert.Contains("fallback_file_proof_missing", summary.Reasons);
    }

    [Fact]
    public void TunaSoakMatrixSummary_PassesRecoveredUnexpectedFallbackWithWarning()
    {
        var cell = new TunaSoakMatrixCell(
            TunaSoakTier.Core,
            "core-tuna-high-helpee-none",
            Phase3TransportMode.Tuna,
            TunaSoakTrafficProfile.MixedScreenFile,
            TunaSoakPreset.HighQuality,
            TunaSoakPayerMode.HelpeeOnly,
            TunaSoakFaultMode.None);
        var result = new TunaSoakCellResult
        {
            CellId = cell.CellId,
            Tier = cell.Tier,
            Transport = cell.Transport,
            TrafficProfile = cell.TrafficProfile,
            Preset = cell.Preset,
            Payer = cell.Payer,
            Fault = cell.Fault,
            Completed = true,
            SessionAlive = true,
            ChatControlAlive = true,
            FileCompleted = true,
            ScreenCompleted = true,
            FileBytesSent = 1000,
            FileBytesReceived = 990,
            FileReceiveRatio = 0.99,
            TunaFrameCount = 10,
            FallbackExpected = false,
            FallbackStarted = true,
            FallbackFileSent = true,
            FallbackFileReceived = true,
            FallbackScreenSent = true,
            FallbackScreenReceived = true,
            TerminalReason = "sidecar_remote_closed",
            WarningReason = "unexpected_tuna_drop_recovered:file_receive_ratio=0.9900; terminal_reason=sidecar_remote_closed",
        };

        var summary = TunaSoakMatrixSummary.Build(new[] { result });

        Assert.Equal("pass", summary.Verdict);
        Assert.Empty(summary.Reasons);
        Assert.Contains("core-tuna-high-helpee-none:warning:unexpected_tuna_drop_recovered:file_receive_ratio=0.9900; terminal_reason=sidecar_remote_closed", summary.Warnings);
    }

    [Fact]
    public void TunaSoakMatrixSummary_FailsFileTrafficWhenFileDidNotComplete()
    {
        var cell = new TunaSoakMatrixCell(
            TunaSoakTier.Core,
            "core-tuna-file-helpee-switch-off",
            Phase3TransportMode.Tuna,
            TunaSoakTrafficProfile.FileOnly,
            TunaSoakPreset.HighQuality,
            TunaSoakPayerMode.HelpeeOnly,
            TunaSoakFaultMode.SwitchOffFallback);
        var result = new TunaSoakCellResult
        {
            CellId = cell.CellId,
            Tier = cell.Tier,
            Transport = cell.Transport,
            TrafficProfile = cell.TrafficProfile,
            Preset = cell.Preset,
            Payer = cell.Payer,
            Fault = cell.Fault,
            Completed = true,
            SessionAlive = true,
            ChatControlAlive = true,
            FileCompleted = false,
            ScreenCompleted = true,
            FileBytesSent = 128_827_392,
            FileBytesReceived = 124_895_232,
            FileReceiveRatio = 0.9695,
            TunaFrameCount = 10,
            FallbackExpected = true,
            FallbackStarted = true,
            FallbackFileSent = true,
            FallbackFileReceived = true,
            TerminalReason = "local_ipc_eof",
        };

        var summary = TunaSoakMatrixSummary.Build(new[] { result });

        Assert.Equal("fail", summary.Verdict);
        Assert.Contains("core-tuna-file-helpee-switch-off:file_incomplete", summary.Reasons);
    }

    [Fact]
    public void TunaSoakMatrixSummary_FailsUnexpectedFallbackWithoutProof()
    {
        var cell = new TunaSoakMatrixCell(
            TunaSoakTier.Core,
            "core-tuna-high-both-none",
            Phase3TransportMode.Tuna,
            TunaSoakTrafficProfile.MixedScreenFile,
            TunaSoakPreset.HighQuality,
            TunaSoakPayerMode.BothUnlocked,
            TunaSoakFaultMode.None);
        var result = new TunaSoakCellResult
        {
            CellId = cell.CellId,
            Tier = cell.Tier,
            Transport = cell.Transport,
            TrafficProfile = cell.TrafficProfile,
            Preset = cell.Preset,
            Payer = cell.Payer,
            Fault = cell.Fault,
            Completed = true,
            SessionAlive = true,
            ChatControlAlive = true,
            FileBytesSent = 1000,
            FileBytesReceived = 998,
            FileReceiveRatio = 0.998,
            TunaFrameCount = 10,
            FallbackExpected = false,
            FallbackStarted = true,
            FallbackFileSent = false,
            FallbackFileReceived = false,
            FallbackScreenSent = false,
            FallbackScreenReceived = false,
        };

        var summary = TunaSoakMatrixSummary.Build(new[] { result });

        Assert.Equal("fail", summary.Verdict);
        Assert.Contains("core-tuna-high-both-none:unexpected_fallback_proof_missing", summary.Reasons);
    }

    [Fact]
    public void TunaSoakRetryWarningIsCarriedIntoFinalResult()
    {
        var failedAttempt = new TunaSoakCellResult
        {
            FailureReason = "file_no_progress:file_tuna_lane_unavailable",
            WarningReason = "tuna_diagnostic_counter_lost_after_reset:sidecar_forwarded_frames=4",
        };

        var warning = BuildRetryWarning(1, failedAttempt);
        var finalWarning = AppendSoakWarnings(string.Empty, new[] { warning });

        Assert.Contains("retry_after_failed_attempt:1", finalWarning);
        Assert.Contains("previous_failure=file_no_progress:file_tuna_lane_unavailable", finalWarning);
        Assert.Contains("previous_warning=tuna_diagnostic_counter_lost_after_reset", finalWarning);
    }

    [Fact]
    public void TunaSoakSidecarJsonHelpersExtractTerminalAndProviderState()
    {
        var stdout = new ConcurrentQueue<string>();
        stdout.Enqueue("{\"event\":\"ready\"}");
        var start = stdout.Count;
        stdout.Enqueue("{\"event\":\"provider_paths_degraded\",\"usableCount\":3}");
        stdout.Enqueue("{\"event\":\"tuna_bridge_terminal\",\"terminalReason\":\"tuna_stream_eof\"}");

        var slice = ReadListenerStdoutSlice(stdout, start);

        Assert.Contains("\"event\":\"provider_paths_degraded\"", slice, StringComparison.Ordinal);
        Assert.Equal("tuna_stream_eof", ExtractLastJsonString(slice, "terminalReason"));
    }

    [Fact]
    public void TunaSoakUnexpectedFallbackRecovered_RequiresProofAndHighReceiveRatio()
    {
        var cell = new TunaSoakMatrixCell(
            TunaSoakTier.Core,
            "core-tuna-high-helpee-none",
            Phase3TransportMode.Tuna,
            TunaSoakTrafficProfile.MixedScreenFile,
            TunaSoakPreset.HighQuality,
            TunaSoakPayerMode.HelpeeOnly,
            TunaSoakFaultMode.None);

        Assert.True(IsUnexpectedTunaFallbackRecovered(cell, fallbackExpected: false, fallbackStarted: true, fallbackFileSent: true, fallbackFileReceived: true, fallbackScreenSent: true, fallbackScreenReceived: true, fileBytesSent: 1000, fileBytesReceived: 990, sessionAlive: true));
        Assert.False(IsUnexpectedTunaFallbackRecovered(cell, fallbackExpected: false, fallbackStarted: true, fallbackFileSent: true, fallbackFileReceived: true, fallbackScreenSent: true, fallbackScreenReceived: false, fileBytesSent: 1000, fileBytesReceived: 990, sessionAlive: true));
        Assert.False(IsUnexpectedTunaFallbackRecovered(cell, fallbackExpected: false, fallbackStarted: true, fallbackFileSent: true, fallbackFileReceived: true, fallbackScreenSent: true, fallbackScreenReceived: true, fileBytesSent: 1000, fileBytesReceived: 900, sessionAlive: true));
        Assert.False(IsUnexpectedTunaFallbackRecovered(cell, fallbackExpected: true, fallbackStarted: true, fallbackFileSent: true, fallbackFileReceived: true, fallbackScreenSent: true, fallbackScreenReceived: true, fileBytesSent: 1000, fileBytesReceived: 990, sessionAlive: true));

        var restartBeforeTrafficCell = cell with
        {
            CellId = "core-tuna-high-helpee-restart",
            Fault = TunaSoakFaultMode.AppRestartBeforeTraffic,
        };
        Assert.True(IsUnexpectedTunaFallbackRecovered(restartBeforeTrafficCell, fallbackExpected: false, fallbackStarted: true, fallbackFileSent: true, fallbackFileReceived: true, fallbackScreenSent: true, fallbackScreenReceived: true, fileBytesSent: 1000, fileBytesReceived: 990, sessionAlive: true));
    }

    [Trait("Category", "Manual")]
    [ManualBridgeFact]
    public async Task TunaSidecar_SoakMatrix_FileScreenAcrossPayersPresetsFaults()
    {
        if (!IsEnabled(SoakOptInEnv))
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var sidecarExe = Path.Combine(repoRoot, "artifacts", "tuna-sidecar", "nlink-tuna-sidecar.exe");
        var walletPath = Path.Combine(repoRoot, "artifacts", "tuna-poc", "wallet-test-nkn.json");
        var bridgeDir = TryFindBridgeBundleDirectory();
        var walletPassword = Environment.GetEnvironmentVariable(Phase3WalletPasswordEnv);
        var options = TunaSoakMatrixOptions.Load();
        var cells = TunaSoakMatrixCell.Build(options);
        var phase3Options = options.ToPhase3BenchmarkOptions(maxDurationOverrideSec: null);
        var prerequisite = ValidatePhase3TunaPrerequisites(Phase3TransportMode.Tuna, sidecarExe, walletPath, walletPassword, phase3Options);

        Assert.True(File.Exists(sidecarExe), $"Missing Tuna sidecar: {sidecarExe}");
        Assert.True(File.Exists(walletPath), $"Missing Tuna test wallet: {Path.GetFileName(walletPath)}");
        Assert.True(bridgeDir is not null, "Bridge runtime not found. Build artifacts/bridge/win-x64 first.");
        Assert.True(prerequisite.IsValid, $"Tuna soak prerequisites failed: {prerequisite.Reason}");

        var artifactDir = Path.Combine(
            repoRoot,
            "artifacts",
            "tuna-sidecar",
            "soak-matrix-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(artifactDir);
        var runsPath = Path.Combine(artifactDir, "runs.jsonl");
        var appLogStart = GetOperationalLogLength();
        var listenerStdout = new ConcurrentQueue<string>();
        var listenerStderr = new ConcurrentQueue<string>();
        var previousNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var previousBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var previousManualBridge = Environment.GetEnvironmentVariable("NLINK_RUN_MANUAL_BRIDGE");
        var results = new List<TunaSoakCellResult>();
        TunaSoakMatrixSummary? summary = null;
        Exception? terminalException = null;
        var matrixStartedAtUtc = DateTimeOffset.UtcNow;

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", Path.Combine(bridgeDir!, "node.exe"));
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", Path.Combine(bridgeDir!, "index.js"));
            Environment.SetEnvironmentVariable("NLINK_RUN_MANUAL_BRIDGE", "1");

            using var cts = new CancellationTokenSource(options.TotalTimeout);
            try
            {
                await AppendPhase3EventAsync(
                    runsPath,
                    new
                    {
                        @event = "soak_matrix_start",
                        options = options.ToArtifactModel(),
                        cellCount = cells.Count,
                        walletFile = Path.GetFileName(walletPath),
                        bridgeRuntime = Path.GetFileName(bridgeDir!),
                        startedAtUtc = DateTimeOffset.UtcNow,
                    },
                    cts.Token);

                foreach (var cell in cells)
                {
                    TunaSoakCellResult? result = null;
                    var retryWarnings = new List<string>();
                    for (var attempt = 1; attempt <= options.MaxCellRetries + 1; attempt++)
                    {
                        result = await RunTunaSoakCellAsync(
                            cell,
                            options,
                            sidecarExe,
                            walletPath,
                            walletPassword!,
                            runsPath,
                            listenerStdout,
                            listenerStderr,
                            cts.Token);
                        if (!ShouldRetrySoakCell(cell, result, attempt, options.MaxCellRetries))
                        {
                            break;
                        }

                        await AppendPhase3EventAsync(
                            runsPath,
                            new
                            {
                                @event = "soak_cell_retry",
                                cellId = cell.CellId,
                                attempt,
                                maxRetries = options.MaxCellRetries,
                                reason = result.FailureReason,
                                result,
                                retryAtUtc = DateTimeOffset.UtcNow,
                            },
                            cts.Token);
                        retryWarnings.Add(BuildRetryWarning(attempt, result));
                        await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
                    }

                    Assert.NotNull(result);
                    if (retryWarnings.Count > 0)
                    {
                        result.WarningReason = AppendSoakWarnings(result.WarningReason, retryWarnings);
                    }

                    results.Add(result);
                    await AppendPhase3EventAsync(runsPath, result, cts.Token);
                }
            }
            catch (Exception ex)
            {
                terminalException = ex;
                await AppendPhase3EventAsync(
                    runsPath,
                    new
                    {
                        @event = "soak_matrix_aborted",
                        error = ex.GetType().Name + ":" + ex.Message,
                        completedCells = results.Count,
                        cellCount = cells.Count,
                        abortedAtUtc = DateTimeOffset.UtcNow,
                    },
                    CancellationToken.None);
            }
            finally
            {
                summary = TunaSoakMatrixSummary.Build(results);
                await File.WriteAllTextAsync(
                    Path.Combine(artifactDir, "summary.json"),
                    JsonSerializer.Serialize(summary, SoakJsonOptions),
                    CancellationToken.None);
                await File.WriteAllTextAsync(
                    Path.Combine(artifactDir, "app-log-tail.redacted.log"),
                    RedactPhase3ArtifactText(ReadTunaSoakOperationalLogSlice(appLogStart, matrixStartedAtUtc), walletPath, walletPassword),
                    CancellationToken.None);
                await File.WriteAllLinesAsync(
                    Path.Combine(artifactDir, "listener.stdout.redacted.jsonl"),
                    listenerStdout.Select(line => RedactPhase3ArtifactText(line, walletPath, walletPassword)),
                    CancellationToken.None);
                await File.WriteAllLinesAsync(
                    Path.Combine(artifactDir, "listener.stderr.redacted.log"),
                    listenerStderr.Select(line => RedactPhase3ArtifactText(line, walletPath, walletPassword)),
                    CancellationToken.None);
            }

            Assert.True(terminalException is null, $"Tuna soak matrix aborted after {results.Count}/{cells.Count} cells: {terminalException?.GetType().Name}:{terminalException?.Message}. Artifacts: {artifactDir}");
            Assert.NotEqual("fail", summary.Verdict);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", previousNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", previousBridgePath);
            Environment.SetEnvironmentVariable("NLINK_RUN_MANUAL_BRIDGE", previousManualBridge);
        }
    }

    private async Task<TunaSoakCellResult> RunTunaSoakCellAsync(
        TunaSoakMatrixCell cell,
        TunaSoakMatrixOptions options,
        string sidecarExe,
        string walletPath,
        string walletPassword,
        string runsPath,
        ConcurrentQueue<string> listenerStdout,
        ConcurrentQueue<string> listenerStderr,
        CancellationToken ct)
    {
        var startedAtUtc = DateTimeOffset.UtcNow;
        var logStart = GetOperationalLogLength();
        var listenerStdoutStart = listenerStdout.Count;
        var phase3Options = options.ToPhase3BenchmarkOptions(
            cell.Fault == TunaSoakFaultMode.ProviderTimeout
                ? Math.Max(30, (int)Math.Min(options.CellDuration.TotalSeconds / 2, 120))
                : null);
        if (cell.Fault == TunaSoakFaultMode.CapReached)
        {
            phase3Options = phase3Options with
            {
                ListenerMaxTotalMiB = 64,
                ListenerMaxDurationSec = Math.Max(90, Math.Min(phase3Options.ListenerMaxDurationSec, 180)),
            };
        }

        await AppendPhase3EventAsync(
            runsPath,
            new
            {
                @event = "soak_cell_start",
                cellId = cell.CellId,
                tier = cell.Tier,
                transport = cell.Transport,
                trafficProfile = cell.TrafficProfile,
                preset = cell.Preset,
                payer = cell.Payer,
                fault = cell.Fault,
                networkLabel = options.NetworkLabel,
                networkPairId = options.NetworkPairId,
                presetMetadata = GetTunaSoakPresetMetadata(cell.Preset),
                startedAtUtc,
            },
            ct);

        try
        {
            if (cell.Fault == TunaSoakFaultMode.AppRestartBeforeTraffic)
            {
                using var warmup = await CreateTunaSoakLiveRunContextAsync(
                    cell with { Fault = TunaSoakFaultMode.None },
                    phase3Options,
                    sidecarExe,
                    walletPath,
                    walletPassword,
                    runsPath,
                    listenerStdout,
                    listenerStderr,
                    setupRepeat: -1,
                    ct);
                await AppendPhase3EventAsync(
                    runsPath,
                    new { @event = "soak_app_restart_simulated", cellId = cell.CellId, phase = "pre_traffic_context_disposed" },
                    ct);
            }

            using var context = await CreateTunaSoakLiveRunContextAsync(
                cell,
                phase3Options,
                sidecarExe,
                walletPath,
                walletPassword,
                runsPath,
                listenerStdout,
                listenerStderr,
                setupRepeat: Math.Abs(cell.CellId.GetHashCode(StringComparison.Ordinal)),
                ct);

            var result = await RunTunaSoakConcurrentTrafficAsync(
                context,
                cell,
                options,
                phase3Options,
                runsPath,
                startedAtUtc,
                logStart,
                listenerStdout,
                listenerStdoutStart,
                ct);
            result.StartedUtc = startedAtUtc;
            result.EndedUtc = DateTimeOffset.UtcNow;
            result.DurationMs = Math.Max(1, (long)(result.EndedUtc - result.StartedUtc).TotalMilliseconds);
            result.LogExcerpt = ExtractTunaSoakProofExcerpt(logStart, startedAtUtc);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TunaSoakCellResult
            {
                CellId = cell.CellId,
                Tier = cell.Tier,
                Transport = cell.Transport,
                TrafficProfile = cell.TrafficProfile,
                Preset = cell.Preset,
                Payer = cell.Payer,
                Fault = cell.Fault,
                StartedUtc = startedAtUtc,
                EndedUtc = DateTimeOffset.UtcNow,
                DurationMs = Math.Max(1, (long)(DateTimeOffset.UtcNow - startedAtUtc).TotalMilliseconds),
                FailureReason = ex.GetType().Name + ":" + ex.Message,
                LogExcerpt = ExtractTunaSoakProofExcerpt(logStart, startedAtUtc),
            };
        }
    }

    private async Task<Phase3LiveRunContext> CreateTunaSoakLiveRunContextAsync(
        TunaSoakMatrixCell cell,
        Phase3BenchmarkOptions options,
        string sidecarExe,
        string walletPath,
        string walletPassword,
        string runsPath,
        ConcurrentQueue<string> listenerStdout,
        ConcurrentQueue<string> listenerStderr,
        int setupRepeat,
        CancellationToken ct)
    {
        if (cell.Transport != Phase3TransportMode.Tuna || cell.Payer is TunaSoakPayerMode.HelpeeOnly or TunaSoakPayerMode.BothUnlocked)
        {
            return await CreatePhase3LiveRunContextWithRetryAsync(
                cell.Transport,
                setupRepeat,
                options,
                sidecarExe,
                walletPath,
                walletPassword,
                runsPath,
                listenerStdout,
                listenerStderr,
                ct);
        }

        return await CreateTunaSoakHelperPaysLiveRunContextAsync(
            cell,
            options,
            sidecarExe,
            walletPath,
            walletPassword,
            runsPath,
            listenerStdout,
            listenerStderr,
            setupRepeat,
            ct);
    }

    private async Task<Phase3LiveRunContext> CreateTunaSoakHelperPaysLiveRunContextAsync(
        TunaSoakMatrixCell cell,
        Phase3BenchmarkOptions options,
        string sidecarExe,
        string walletPath,
        string walletPassword,
        string runsPath,
        ConcurrentQueue<string> listenerStdout,
        ConcurrentQueue<string> listenerStderr,
        int setupRepeat,
        CancellationToken ct)
    {
        var identityDir = Path.Combine(Path.GetTempPath(), "nlink-soak-tuna-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(identityDir);
        Process? listenerProcess = null;
        RealNknClientAdapter? hostClient = null;
        RealNknClientAdapter? helperClient = null;
        NknSignalingTransport? host = null;
        NknSignalingTransport? helper = null;
        var setupStage = "identity";
        try
        {
            await AppendPhase3SetupEventAsync(runsPath, Phase3TransportMode.Tuna, setupRepeat, 1, 1, setupStage, "started", 0, string.Empty, string.Empty, 0, ct);
            var hostKey = Path.Combine(identityDir, "helpee-identity.json");
            var helperKey = Path.Combine(identityDir, "helper-identity.json");
            var hostOptionsBase = LoadNknOptionsWithOverrides(hostKey, "nlink-soak-helpee-" + Guid.NewGuid().ToString("N")[..8]);
            var helperOptionsBase = LoadNknOptionsWithOverrides(helperKey, "nlink-soak-helper-" + Guid.NewGuid().ToString("N")[..8]);
            var hostIdentity = NknIdentityStore.LoadOrCreate(hostOptionsBase);
            var helperIdentity = NknIdentityStore.LoadOrCreate(helperOptionsBase);
            hostClient = new RealNknClientAdapter(hostIdentity, hostOptionsBase);
            helperClient = new RealNknClientAdapter(helperIdentity, helperOptionsBase);
            await AppendPhase3SetupEventAsync(runsPath, Phase3TransportMode.Tuna, setupRepeat, 1, 1, setupStage, "succeeded", 0, string.Empty, string.Empty, 0, ct);

            setupStage = "resolve_dialer_address";
            await AppendPhase3SetupEventAsync(runsPath, Phase3TransportMode.Tuna, setupRepeat, 1, 1, setupStage, "started", 0, string.Empty, string.Empty, 0, ct);
            var hostSeedBase64 = NknIdentityStore.ReadSeedBase64ForConnect(hostOptionsBase.KeyPath);
            Assert.False(string.IsNullOrWhiteSpace(hostSeedBase64), "Helpee identity seed is required for helper-paid Tuna dialer identity.");
            var hostSidecarAddress = await ResolveSidecarAddressAsync(sidecarExe, hostSeedBase64!, ct);
            await AppendPhase3SetupEventAsync(runsPath, Phase3TransportMode.Tuna, setupRepeat, 1, 1, setupStage, "succeeded", 0, string.Empty, string.Empty, hostSidecarAddress.Length, ct);

            setupStage = "listener_ready";
            await AppendPhase3SetupEventAsync(runsPath, Phase3TransportMode.Tuna, setupRepeat, 1, 1, setupStage, "started", 0, string.Empty, string.Empty, 0, ct);
            var listenerReady = await StartListenerSidecarAsync(
                sidecarExe,
                walletPath,
                walletPassword,
                hostSidecarAddress,
                listenerStdout,
                listenerStderr,
                ct,
                maxTotalMiB: options.ListenerMaxTotalMiB,
                maxDurationSec: options.ListenerMaxDurationSec,
                acceptTimeoutSec: options.ListenerAcceptTimeoutSec,
                maxPriceNknPerMb: Phase3MaxPriceNknPerMb,
                identifier: "nlink-soak-helper-listener-" + Guid.NewGuid().ToString("N")[..8]);
            listenerProcess = Process.GetProcessById(listenerReady.ProcessId);
            await AppendPhase3SetupEventAsync(runsPath, Phase3TransportMode.Tuna, setupRepeat, 1, 1, setupStage, "succeeded", 0, string.Empty, listenerReady.LocalIpc, listenerReady.Address.Length, ct);

            var hostTunaOptions = CreateTunaOptionsForLiveTest(listenerEndpoint: null, sidecarExe, hostSeedBase64);
            var helperTunaOptions = CreateTunaOptionsForLiveTest(listenerReady.LocalIpc, sidecarExePath: null);

            setupStage = "transport_construct";
            await AppendPhase3SetupEventAsync(runsPath, Phase3TransportMode.Tuna, setupRepeat, 1, 1, setupStage, "started", 0, string.Empty, string.Empty, 0, ct);
            host = new NknSignalingTransport(hostClient, hostOptionsBase, hostIdentity, hostTunaOptions, new NknTunaAccelerationLane(hostTunaOptions));
            helper = new NknSignalingTransport(helperClient, helperOptionsBase, helperIdentity, helperTunaOptions, new NknTunaAccelerationLane(helperTunaOptions));
            await AppendPhase3SetupEventAsync(runsPath, Phase3TransportMode.Tuna, setupRepeat, 1, 1, setupStage, "succeeded", 0, string.Empty, string.Empty, 0, ct);

            setupStage = "session_approval";
            await AppendPhase3SetupEventAsync(runsPath, Phase3TransportMode.Tuna, setupRepeat, 1, 1, setupStage, "started", 0, string.Empty, string.Empty, 0, ct);
            var sessionId = await ApproveLiveSessionAsync(
                host,
                helper,
                InviteCapabilities.Chat | InviteCapabilities.ScreenShare | InviteCapabilities.FileTransfer,
                ct);
            await AppendPhase3SetupEventAsync(runsPath, Phase3TransportMode.Tuna, setupRepeat, 1, 1, setupStage, "succeeded", 0, string.Empty, string.Empty, 0, ct);

            setupStage = "acceleration_negotiation";
            await AppendPhase3SetupEventAsync(runsPath, Phase3TransportMode.Tuna, setupRepeat, 1, 1, setupStage, "started", 0, string.Empty, listenerReady.LocalIpc, listenerReady.Address.Length, ct);
            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(120));
            await AppendPhase3SetupEventAsync(runsPath, Phase3TransportMode.Tuna, setupRepeat, 1, 1, setupStage, "succeeded", 0, string.Empty, listenerReady.LocalIpc, listenerReady.Address.Length, ct);

            var context = new Phase3LiveRunContext(
                Phase3TransportMode.Tuna,
                identityDir,
                sessionId,
                host,
                helper,
                hostClient,
                helperClient,
                listenerProcess,
                listenerReady);
            host = null;
            helper = null;
            hostClient = null;
            helperClient = null;
            listenerProcess = null;
            return context;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ex.Data["phase3_setup_stage"] ??= setupStage;
            host?.Dispose();
            helper?.Dispose();
            hostClient?.Dispose();
            helperClient?.Dispose();
            if (listenerProcess is not null)
            {
                TryKill(listenerProcess);
            }

            try { Directory.Delete(identityDir, recursive: true); } catch { }
            throw;
        }
    }

    private async Task<TunaSoakCellResult> RunTunaSoakConcurrentTrafficAsync(
        Phase3LiveRunContext context,
        TunaSoakMatrixCell cell,
        TunaSoakMatrixOptions soakOptions,
        Phase3BenchmarkOptions phase3Options,
        string runsPath,
        DateTimeOffset startedAtUtc,
        int logStart,
        ConcurrentQueue<string> listenerStdout,
        int listenerStdoutStart,
        CancellationToken ct)
    {
        var fileRunId = "soak-file-" + cell.CellId + "-" + Guid.NewGuid().ToString("N")[..8];
        var screenRunId = "soak-screen-" + cell.CellId + "-" + Guid.NewGuid().ToString("N")[..8];
        var faultTask = ScheduleTunaSoakFaultAsync(context, cell, soakOptions, runsPath, ct);
        var fileTask = CellUsesFileTraffic(cell)
            ? RunPhase3FileProfileAsync(context, fileRunId, repeat: 1, phase3Options, logStart, ct)
            : Task.FromResult(SkippedSoakRun(fileRunId, Phase3Profile.File, context.Mode));
        var screenTask = CellUsesScreenTraffic(cell)
            ? RunPhase3ScreenProfileAsync(context, screenRunId, repeat: 1, phase3Options, logStart, runsPath, ct)
            : Task.FromResult(SkippedSoakRun(screenRunId, Phase3Profile.Screen, context.Mode));
        await Task.WhenAll(fileTask, screenTask, faultTask);

        var file = await fileTask;
        var screen = await screenTask;
        var logTail = ReadTunaSoakOperationalLogSlice(logStart, startedAtUtc);
        var sidecarText = ReadListenerStdoutSlice(listenerStdout, listenerStdoutStart);
        var fallbackExpected = cell.Transport == Phase3TransportMode.Tuna &&
                               cell.Fault is TunaSoakFaultMode.SidecarCrash or TunaSoakFaultMode.SwitchOffFallback or TunaSoakFaultMode.ProviderTimeout or TunaSoakFaultMode.CapReached;
        var fallbackFileSent = CountOccurrences(logTail, "event=tuna_fallback_nkn_frame_sent; message_type=file_transfer_data_frame") > 0;
        var fallbackFileReceived = CountOccurrences(logTail, "event=tuna_fallback_nkn_frame_received; message_type=file_transfer_data_frame") > 0;
        var fallbackScreenSent = CountOccurrences(logTail, "event=tuna_fallback_nkn_frame_sent; message_type=screenshare_frame") > 0;
        var fallbackScreenReceived = CountOccurrences(logTail, "event=tuna_fallback_nkn_frame_received; message_type=screenshare_frame") > 0;
        var fallbackStarted = CountOccurrences(logTail, "event=tuna_fallback_started") > 0 ||
                              fallbackFileSent ||
                              fallbackFileReceived ||
                              fallbackScreenSent ||
                              fallbackScreenReceived;
        var terminalReason = ExtractLastLogToken(logTail, "terminalReason=");
        if (string.IsNullOrWhiteSpace(terminalReason))
        {
            terminalReason = ExtractLastLogToken(logTail, "sidecar_event=tuna_bridge_terminal; sidecar_reason=");
        }

        if (string.IsNullOrWhiteSpace(terminalReason))
        {
            terminalReason = ExtractLastLogToken(logTail, "sidecar_reason=");
        }

        var providerDegraded = CountOccurrences(logTail, "sidecar_event=provider_paths_degraded") > 0 ||
                               CountOccurrences(logTail, "\"event\":\"provider_paths_degraded\"") > 0 ||
                               CountOccurrences(logTail, "event=provider_paths_degraded") > 0 ||
                               CountOccurrences(sidecarText, "\"event\":\"provider_paths_degraded\"") > 0;
        if (string.IsNullOrWhiteSpace(terminalReason))
        {
            terminalReason = ExtractLastJsonString(sidecarText, "terminalReason");
        }
        var fallbackProofComplete = fallbackStarted &&
                                    (!CellUsesFileTraffic(cell) || fallbackFileSent && fallbackFileReceived) &&
                                    (!CellUsesScreenTraffic(cell) || fallbackScreenSent && fallbackScreenReceived);
        var tunaDiagnosticFrames = file.TunaFrameCount + screen.TunaFrameCount;
        var tunaLogFrames = CountOccurrences(logTail, "sidecar_event=bridge_frame_forwarded");
        var tunaFrames = Math.Max(tunaDiagnosticFrames, tunaLogFrames);
        var sessionAlive = IsPhase3SessionAlive(context);
        var fallbackOk = !fallbackExpected || fallbackProofComplete;
        var tunaOk = cell.Transport != Phase3TransportMode.Tuna || tunaFrames > 0 || fallbackExpected;
        var fileReceiveRatio = ComputeSoakFileReceiveRatio(file.BytesSent, file.BytesReceived);
        var unexpectedFallbackStarted = cell.Transport == Phase3TransportMode.Tuna &&
                                        !fallbackExpected &&
                                        fallbackStarted;
        var unexpectedFallbackRecovered = IsUnexpectedTunaFallbackRecovered(
            cell,
            fallbackExpected,
            fallbackStarted,
            fallbackFileSent,
            fallbackFileReceived,
            fallbackScreenSent,
            fallbackScreenReceived,
            file.BytesSent,
            file.BytesReceived,
            sessionAlive);
        var warnings = new List<string>();
        if (unexpectedFallbackRecovered)
        {
            warnings.Add(string.Format(CultureInfo.InvariantCulture, "unexpected_tuna_drop_recovered:file_receive_ratio={0:F4}; terminal_reason={1}; file_failure={2}", fileReceiveRatio, string.IsNullOrWhiteSpace(terminalReason) ? "unknown" : terminalReason, file.FailureReason));
        }

        if (cell.Transport == Phase3TransportMode.Tuna && tunaDiagnosticFrames <= 0 && tunaLogFrames > 0)
        {
            warnings.Add(string.Format(CultureInfo.InvariantCulture, "tuna_diagnostic_counter_lost_after_reset:sidecar_forwarded_frames={0}", tunaLogFrames));
        }

        if (cell.Transport == Phase3TransportMode.Tuna && providerDegraded)
        {
            warnings.Add("provider_paths_degraded");
        }

        var warningReason = string.Join("; ", warnings);
        var failureReason = string.Empty;
        if (CellUsesFileTraffic(cell) && file.BytesReceived <= 0)
        {
            failureReason = "file_no_progress:" + file.FailureReason;
        }
        else if (CellUsesFileTraffic(cell) && !file.Completed)
        {
            failureReason = string.Format(
                CultureInfo.InvariantCulture,
                "file_incomplete:receive_ratio={0:F4}; reason={1}",
                fileReceiveRatio,
                string.IsNullOrWhiteSpace(file.FailureReason) ? "not_completed" : file.FailureReason);
        }
        else if (unexpectedFallbackStarted && !fallbackProofComplete)
        {
            failureReason = "unexpected_fallback_proof_missing";
        }
        else if (CellUsesFileTraffic(cell) &&
                 unexpectedFallbackStarted &&
                 fileReceiveRatio < SoakUnexpectedFallbackRecoveredMinFileRatio)
        {
            failureReason = string.Format(CultureInfo.InvariantCulture, "unexpected_fallback_file_receive_ratio_low:{0:F4}", fileReceiveRatio);
        }
        else if (unexpectedFallbackStarted && string.IsNullOrWhiteSpace(terminalReason))
        {
            failureReason = "unexpected_fallback_terminal_reason_missing";
        }
        else if (CellUsesFileTraffic(cell) &&
                 !fallbackExpected &&
                 !unexpectedFallbackRecovered &&
                 file.StallCount > 0)
        {
            failureReason = "file_stalled:" + file.FailureReason;
        }
        else if (CellUsesFileTraffic(cell) &&
                 !fallbackExpected &&
                 !unexpectedFallbackRecovered &&
                 !string.IsNullOrWhiteSpace(file.FailureReason))
        {
            failureReason = "file_transport_failure:" + file.FailureReason;
        }
        else if (CellUsesScreenTraffic(cell) && unexpectedFallbackRecovered && screen.ReceivedFrames <= 0)
        {
            failureReason = "screen_no_frames_after_recovered_fallback";
        }
        else if (CellUsesScreenTraffic(cell) && !screen.Completed)
        {
            failureReason = "screen_incomplete:" + screen.FailureReason;
        }
        else if (!sessionAlive)
        {
            failureReason = "session_not_alive";
        }
        else if (!tunaOk)
        {
            failureReason = "tuna_readiness_missing";
        }
        else if (!fallbackOk)
        {
            failureReason = "fallback_proof_missing";
        }

        return new TunaSoakCellResult
        {
            CellId = cell.CellId,
            Tier = cell.Tier,
            Transport = cell.Transport,
            TrafficProfile = cell.TrafficProfile,
            Preset = cell.Preset,
            Payer = cell.Payer,
            Fault = cell.Fault,
            Completed = string.IsNullOrWhiteSpace(failureReason),
            SessionAlive = sessionAlive,
            ChatControlAlive = sessionAlive,
            FileCompleted = !CellUsesFileTraffic(cell) || file.Completed,
            ScreenCompleted = !CellUsesScreenTraffic(cell) || screen.Completed,
            FileBytesSent = file.BytesSent,
            FileBytesReceived = file.BytesReceived,
            FileReceiveRatio = fileReceiveRatio,
            FileThroughputMbps = file.ReceiverThroughputMbps,
            ScreenSentFrames = screen.SentFrames,
            ScreenReceivedFrames = screen.ReceivedFrames,
            ScreenLatencyP95Ms = screen.LatencyP95Ms,
            ScreenDropRate = screen.DropRate,
            ScreenStallCount = screen.StallCount,
            TunaFrameCount = tunaFrames,
            TunaDiagnosticFrameCount = tunaDiagnosticFrames,
            TunaLogFrameCount = tunaLogFrames,
            NknFrameCount = file.NknFrameCount + screen.NknFrameCount,
            FallbackExpected = fallbackExpected,
            FallbackStarted = fallbackStarted,
            FallbackFileSent = fallbackFileSent,
            FallbackFileReceived = fallbackFileReceived,
            FallbackScreenSent = fallbackScreenSent,
            FallbackScreenReceived = fallbackScreenReceived,
            TerminalReason = terminalReason,
            ProviderDegraded = providerDegraded,
            FailureReason = failureReason,
            WarningReason = warningReason,
        };
    }

    private static async Task ScheduleTunaSoakFaultAsync(
        Phase3LiveRunContext context,
        TunaSoakMatrixCell cell,
        TunaSoakMatrixOptions options,
        string runsPath,
        CancellationToken ct)
    {
        if (cell.Transport != Phase3TransportMode.Tuna ||
            cell.Fault is TunaSoakFaultMode.None or TunaSoakFaultMode.AppRestartBeforeTraffic)
        {
            return;
        }

        var delay = cell.Fault == TunaSoakFaultMode.ProviderTimeout
            ? TimeSpan.FromSeconds(Math.Max(45, Math.Min(options.CellDuration.TotalSeconds / 2, 150)))
            : TimeSpan.FromSeconds(Math.Max(20, Math.Min(options.CellDuration.TotalSeconds / 3, 90)));
        await Task.Delay(delay, ct);
        await AppendPhase3EventAsync(
            runsPath,
            new { @event = "soak_fault_injected", cellId = cell.CellId, fault = cell.Fault, delayMs = (long)delay.TotalMilliseconds },
            ct);
        if (cell.Fault == TunaSoakFaultMode.SwitchOffFallback)
        {
            await ((ITransportAccelerationControl)context.Host).StopAccelerationAsync("soak_switch_off", ct);
            return;
        }

        if (cell.Fault == TunaSoakFaultMode.SidecarCrash)
        {
            context.KillListener();
        }
    }

    private static string ExtractTunaSoakProofExcerpt(int logStart, DateTimeOffset startedAtUtc)
    {
        var lines = ReadTunaSoakOperationalLogSlice(logStart, startedAtUtc)
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Where(static line =>
                line.Contains("tuna_fallback_", StringComparison.Ordinal) ||
                line.Contains("screenshare_tuna_handoff_", StringComparison.Ordinal) ||
                line.Contains("tuna_mixed_handoff_", StringComparison.Ordinal) ||
                line.Contains("tuna_acceleration_negotiated", StringComparison.Ordinal) ||
                line.Contains("tuna_sidecar_", StringComparison.Ordinal) ||
                line.Contains("tuna_usage_session_recorded", StringComparison.Ordinal))
            .TakeLast(120);
        return string.Join(Environment.NewLine, lines);
    }

    private static string ReadTunaSoakOperationalLogSlice(int activeLogStart, DateTimeOffset startedAtUtc)
    {
        var retained = ReadRetainedOperationalLogsForSoak();
        if (!string.IsNullOrWhiteSpace(retained))
        {
            var threshold = startedAtUtc.ToUniversalTime().AddSeconds(-2);
            var lines = retained
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => TryReadOperationalLogTimestamp(line, out var timestamp) && timestamp >= threshold)
                .ToArray();
            if (lines.Length > 0)
            {
                return string.Join(Environment.NewLine, lines);
            }
        }

        return ReadOperationalLogTail(activeLogStart);
    }

    private static string ReadRetainedOperationalLogsForSoak()
    {
        var activePath = LocalOperationalLog.LogFilePath;
        var directory = Path.GetDirectoryName(activePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return ReadOperationalLogText();
        }

        var files = Directory.GetFiles(directory, "nlink*.log")
            .Where(static path => !Path.GetFileName(path).Contains("Copy", StringComparison.OrdinalIgnoreCase))
            .OrderBy(File.GetLastWriteTimeUtc)
            .ThenBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var builder = new StringBuilder();
        foreach (var file in files)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                builder.AppendLine(reader.ReadToEnd());
            }
            catch
            {
                // Operational logs are best-effort test artifacts; a locked/rotated file must not fail the soak.
            }
        }

        return builder.ToString();
    }

    private static string ReadListenerStdoutSlice(ConcurrentQueue<string> listenerStdout, int startIndex)
    {
        var lines = listenerStdout.ToArray();
        if (startIndex < 0 || startIndex >= lines.Length)
        {
            return string.Empty;
        }

        return string.Join(Environment.NewLine, lines.Skip(startIndex));
    }

    private static string ExtractLastJsonString(string text, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(propertyName))
        {
            return string.Empty;
        }

        var prefix = "\"" + propertyName + "\":\"";
        var index = text.LastIndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return string.Empty;
        }

        var start = index + prefix.Length;
        var end = start;
        while (end < text.Length && text[end] != '"')
        {
            end++;
        }

        return end <= start ? string.Empty : text[start..end].Trim();
    }

    private static bool TryReadOperationalLogTimestamp(string line, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (line.Length < 22 || line[0] != '[' || line[20] != 'Z' || line[21] != ']')
        {
            return false;
        }

        return DateTimeOffset.TryParseExact(
            line.Substring(1, 20),
            "yyyy-MM-dd HH:mm:ss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out timestamp);
    }

    private static bool ShouldRetrySoakCell(TunaSoakMatrixCell cell, TunaSoakCellResult result, int attempt, int maxRetries)
        => attempt <= maxRetries &&
           cell.Transport == Phase3TransportMode.Tuna &&
           cell.Fault == TunaSoakFaultMode.None &&
           !result.Completed &&
           (result.FailureReason.Contains("file_tuna_lane_unavailable", StringComparison.OrdinalIgnoreCase) ||
            result.FailureReason.Contains("tuna_readiness_missing", StringComparison.OrdinalIgnoreCase) ||
            result.FailureReason.Contains("sidecar_remote_closed", StringComparison.OrdinalIgnoreCase) ||
            result.FailureReason.Contains("dialer_exited", StringComparison.OrdinalIgnoreCase));

    private static string BuildRetryWarning(int attempt, TunaSoakCellResult result)
    {
        var reason = string.IsNullOrWhiteSpace(result.FailureReason)
            ? "unknown"
            : result.FailureReason;
        var warning = string.IsNullOrWhiteSpace(result.WarningReason)
            ? string.Empty
            : "; previous_warning=" + result.WarningReason;
        return string.Format(
            CultureInfo.InvariantCulture,
            "retry_after_failed_attempt:{0}; previous_failure={1}{2}",
            attempt,
            reason,
            warning);
    }

    private static string AppendSoakWarnings(string current, IEnumerable<string> warnings)
    {
        var all = new List<string>();
        if (!string.IsNullOrWhiteSpace(current))
        {
            all.Add(current);
        }

        all.AddRange(warnings.Where(static warning => !string.IsNullOrWhiteSpace(warning)));
        return string.Join("; ", all);
    }

    private static bool IsUnexpectedTunaFallbackRecovered(
        TunaSoakMatrixCell cell,
        bool fallbackExpected,
        bool fallbackStarted,
        bool fallbackFileSent,
        bool fallbackFileReceived,
        bool fallbackScreenSent,
        bool fallbackScreenReceived,
        long fileBytesSent,
        long fileBytesReceived,
        bool sessionAlive)
        => cell.Transport == Phase3TransportMode.Tuna &&
           cell.Fault is TunaSoakFaultMode.None or TunaSoakFaultMode.AppRestartBeforeTraffic &&
           !fallbackExpected &&
           fallbackStarted &&
           (!CellUsesFileTraffic(cell) || fallbackFileSent && fallbackFileReceived) &&
           (!CellUsesScreenTraffic(cell) || fallbackScreenSent && fallbackScreenReceived) &&
           sessionAlive &&
           (!CellUsesFileTraffic(cell) ||
            ComputeSoakFileReceiveRatio(fileBytesSent, fileBytesReceived) >= SoakUnexpectedFallbackRecoveredMinFileRatio);

    private static bool CellUsesFileTraffic(TunaSoakMatrixCell cell)
        => cell.TrafficProfile is TunaSoakTrafficProfile.FileOnly or TunaSoakTrafficProfile.MixedScreenFile;

    private static bool CellUsesScreenTraffic(TunaSoakMatrixCell cell)
        => cell.TrafficProfile is TunaSoakTrafficProfile.ScreenOnly or TunaSoakTrafficProfile.MixedScreenFile;

    private static bool CellUsesFileTraffic(TunaSoakCellResult result)
        => result.TrafficProfile is TunaSoakTrafficProfile.FileOnly or TunaSoakTrafficProfile.MixedScreenFile;

    private static bool CellUsesScreenTraffic(TunaSoakCellResult result)
        => result.TrafficProfile is TunaSoakTrafficProfile.ScreenOnly or TunaSoakTrafficProfile.MixedScreenFile;

    private static Phase3RunResult SkippedSoakRun(string runId, Phase3Profile profile, Phase3TransportMode mode)
        => new()
        {
            RunId = runId,
            Profile = profile,
            Mode = mode,
            Repeat = 1,
            Completed = true,
            FailureReason = string.Empty,
        };

    private static double ComputeSoakFileReceiveRatio(long fileBytesSent, long fileBytesReceived)
        => fileBytesSent <= 0
            ? 0
            : Math.Clamp(fileBytesReceived / (double)fileBytesSent, 0, 1);

    private static Dictionary<string, string?> CaptureSoakEnvironment()
        => SoakEnvironmentNames.ToDictionary(static name => name, Environment.GetEnvironmentVariable);

    private static void ClearSoakEnvironment()
    {
        foreach (var name in SoakEnvironmentNames)
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    private static void RestoreSoakEnvironment(IReadOnlyDictionary<string, string?> values)
    {
        foreach (var (name, value) in values)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static readonly JsonSerializerOptions SoakJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly string[] SoakEnvironmentNames =
    [
        SoakOptInEnv,
        SoakDurationMinutesEnv,
        SoakNetworkLabelEnv,
        SoakNetworkPairIdEnv,
        SoakTiersEnv,
        SoakCellFilterEnv,
        SoakFilePacingMbpsEnv,
        SoakCellRetriesEnv,
    ];

    private static object GetTunaSoakPresetMetadata(TunaSoakPreset preset)
        => preset switch
        {
            TunaSoakPreset.TunaQuality => new
            {
                name = "Tuna quality",
                captureFps = 30,
                transportFps = 15,
                maxTransportTarget = "1600x900",
                scale = 1.0,
                qualityProfile = "tuna_quality",
            },
            _ => new
            {
                name = "High quality",
                captureFps = 24,
                transportFps = 15,
                maxTransportTarget = "1440x810",
                scale = 1.0,
                qualityProfile = "normal",
            },
        };

    private sealed record TunaSoakMatrixOptions(
        TimeSpan CellDuration,
        string NetworkLabel,
        string NetworkPairId,
        double FileSendPacingMbps,
        int MaxCellRetries,
        string[] CellFilters,
        TunaSoakTier[] Tiers)
    {
        public TimeSpan TotalTimeout
        {
            get
            {
                var cells = TunaSoakMatrixCell.Build(this);
                var tunaCellCount = cells.Count(static cell => cell.Transport == Phase3TransportMode.Tuna);
                var restartCellCount = cells.Count(static cell => cell.Fault == TunaSoakFaultMode.AppRestartBeforeTraffic);
                var trafficBudgetSeconds = cells.Count * CellDuration.TotalSeconds;
                var tunaSetupBudgetSeconds = tunaCellCount * 420;
                var restartBudgetSeconds = restartCellCount * 180;
                var retryBudgetSeconds = Math.Max(0, MaxCellRetries) * (CellDuration.TotalSeconds + 420);
                return TimeSpan.FromSeconds(Math.Max(900, trafficBudgetSeconds + tunaSetupBudgetSeconds + restartBudgetSeconds + retryBudgetSeconds + 1800));
            }
        }

        public Phase3BenchmarkOptions ToPhase3BenchmarkOptions(int? maxDurationOverrideSec)
        {
            var targetBytes = Math.Max(
                32L * 1024 * 1024,
                (long)(CellDuration.TotalSeconds * FileSendPacingMbps * 1000d * 1000d / 8d));
            var targetMiB = (int)Math.Ceiling(targetBytes / 1024d / 1024d);
            var listenerMaxMiB = Math.Clamp(targetMiB + 512, 512, 8192);
            return new Phase3BenchmarkOptions(
                RepeatCount: 1,
                ProfileDuration: CellDuration,
                FileTargetBytes: targetBytes,
                FileWriteBytes: FileTransferChunkBudget.MaxRawChunkBytes,
                ScreenFps: 15,
                ScreenKeyFrameBytes: 128 * 1024,
                ScreenDeltaFrameBytes: 24 * 1024,
                ListenerMaxTotalMiB: listenerMaxMiB,
                ListenerMaxDurationSec: maxDurationOverrideSec ?? Math.Max(180, (int)CellDuration.TotalSeconds + 120),
                ListenerAcceptTimeoutSec: 180,
                TunaSetupAttempts: 3,
                FileSendPacingMbps: FileSendPacingMbps,
                FileFallbackPacingMbps: Math.Min(2.5, FileSendPacingMbps),
                FileThroughputPassRatio: 1.25,
                ScreenSmokeOnly: false,
                VerboseScreenDiagnostics: false);
        }

        public object ToArtifactModel()
            => new
            {
                durationMin = (int)CellDuration.TotalMinutes,
                networkLabel = NetworkLabel,
                networkPairId = NetworkPairId,
                tiers = Tiers.Select(static tier => tier.ToString().ToLowerInvariant()).ToArray(),
                cellFilters = CellFilters,
                fileSendPacingMbps = FileSendPacingMbps,
                maxCellRetries = MaxCellRetries,
                screenFps = 15,
                note = "Network topology is operator-provided metadata; the harness cannot move machines between networks.",
            };

        public static TunaSoakMatrixOptions Load()
            => new(
                CellDuration: TimeSpan.FromMinutes(ReadInt(SoakDurationMinutesEnv, SoakDefaultDurationMinutes, min: 1, max: 120)),
                NetworkLabel: ReadString(SoakNetworkLabelEnv, "same_machine"),
                NetworkPairId: ReadString(SoakNetworkPairIdEnv, "local"),
                FileSendPacingMbps: ReadDouble(SoakFilePacingMbpsEnv, SoakDefaultFilePacingMbps, min: 1, max: 45),
                MaxCellRetries: ReadInt(SoakCellRetriesEnv, SoakDefaultCellRetries, min: 0, max: 3),
                CellFilters: ReadCsv(SoakCellFilterEnv),
                Tiers: ReadTiers());

        private static int ReadInt(string name, int fallback, int min, int max)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? Math.Clamp(parsed, min, max)
                : fallback;
        }

        private static string ReadString(string name, string fallback)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static double ReadDouble(string name, double fallback, double min, double max)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? Math.Clamp(parsed, min, max)
                : fallback;
        }

        private static string[] ReadCsv(string name)
        {
            var raw = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(raw)
                ? []
                : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
        }

        private static TunaSoakTier[] ReadTiers()
        {
            var raw = Environment.GetEnvironmentVariable(SoakTiersEnv);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return [TunaSoakTier.Core];
            }

            var tiers = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static value => Enum.TryParse<TunaSoakTier>(value, ignoreCase: true, out var tier) ? tier : (TunaSoakTier?)null)
                .Where(static value => value.HasValue)
                .Select(static value => value!.Value)
                .Distinct()
                .ToArray();
            return tiers.Length == 0 ? [TunaSoakTier.Core] : tiers;
        }
    }

    private sealed record TunaSoakMatrixCell(
        TunaSoakTier Tier,
        string CellId,
        Phase3TransportMode Transport,
        TunaSoakTrafficProfile TrafficProfile,
        TunaSoakPreset Preset,
        TunaSoakPayerMode Payer,
        TunaSoakFaultMode Fault)
    {
        public static IReadOnlyList<TunaSoakMatrixCell> Build(TunaSoakMatrixOptions options)
        {
            var cells = new List<TunaSoakMatrixCell>();
            var payers = new[]
            {
                TunaSoakPayerMode.HelpeeOnly,
                TunaSoakPayerMode.HelperOnly,
                TunaSoakPayerMode.BothUnlocked,
            };
            var profiles = new[]
            {
                TunaSoakTrafficProfile.ScreenOnly,
                TunaSoakTrafficProfile.FileOnly,
                TunaSoakTrafficProfile.MixedScreenFile,
            };
            var faults = new[]
            {
                TunaSoakFaultMode.SwitchOffFallback,
                TunaSoakFaultMode.SidecarCrash,
                TunaSoakFaultMode.CapReached,
            };

            foreach (var payer in payers)
            {
                foreach (var profile in profiles)
                {
                    foreach (var fault in faults)
                    {
                        cells.Add(new(
                            TunaSoakTier.Core,
                            $"core-tuna-{FormatSoakProfileId(profile)}-{FormatSoakPayerId(payer)}-{FormatSoakFaultId(fault)}",
                            Phase3TransportMode.Tuna,
                            profile,
                            TunaSoakPreset.TunaQuality,
                            payer,
                            fault));
                    }
                }
            }

            if (options.Tiers.Contains(TunaSoakTier.Extended))
            {
                cells.Add(new(TunaSoakTier.Extended, "extended-tuna-mixed-helpee-provider-timeout", Phase3TransportMode.Tuna, TunaSoakTrafficProfile.MixedScreenFile, TunaSoakPreset.TunaQuality, TunaSoakPayerMode.HelpeeOnly, TunaSoakFaultMode.ProviderTimeout));
                cells.Add(new(TunaSoakTier.Extended, "extended-tuna-mixed-both-provider-timeout", Phase3TransportMode.Tuna, TunaSoakTrafficProfile.MixedScreenFile, TunaSoakPreset.TunaQuality, TunaSoakPayerMode.BothUnlocked, TunaSoakFaultMode.ProviderTimeout));
            }

            var filtered = cells.Where(cell => options.Tiers.Contains(cell.Tier));
            if (options.CellFilters.Length > 0)
            {
                var requested = options.CellFilters.ToHashSet(StringComparer.OrdinalIgnoreCase);
                filtered = filtered.Where(cell => requested.Contains(cell.CellId));
            }

            return filtered.ToArray();
        }

        private static string FormatSoakProfileId(TunaSoakTrafficProfile profile)
            => profile switch
            {
                TunaSoakTrafficProfile.ScreenOnly => "screen",
                TunaSoakTrafficProfile.FileOnly => "file",
                _ => "mixed",
            };

        private static string FormatSoakPayerId(TunaSoakPayerMode payer)
            => payer switch
            {
                TunaSoakPayerMode.HelperOnly => "helper",
                TunaSoakPayerMode.BothUnlocked => "both",
                _ => "helpee",
            };

        private static string FormatSoakFaultId(TunaSoakFaultMode fault)
            => fault switch
            {
                TunaSoakFaultMode.SidecarCrash => "sidecar-kill",
                TunaSoakFaultMode.CapReached => "cap",
                _ => "switch-off",
            };
    }

    private sealed class TunaSoakCellResult
    {
        public string Event { get; init; } = "soak_cell_summary";
        public string CellId { get; init; } = string.Empty;
        public TunaSoakTier Tier { get; init; }
        public Phase3TransportMode Transport { get; init; }
        public TunaSoakTrafficProfile TrafficProfile { get; init; }
        public TunaSoakPreset Preset { get; init; }
        public TunaSoakPayerMode Payer { get; init; }
        public TunaSoakFaultMode Fault { get; init; }
        public DateTimeOffset StartedUtc { get; set; }
        public DateTimeOffset EndedUtc { get; set; }
        public long DurationMs { get; set; }
        public bool Completed { get; init; }
        public bool SessionAlive { get; init; }
        public bool ChatControlAlive { get; init; }
        public bool FileCompleted { get; init; }
        public bool ScreenCompleted { get; init; }
        public long FileBytesSent { get; init; }
        public long FileBytesReceived { get; init; }
        public double FileReceiveRatio { get; init; }
        public double FileThroughputMbps { get; init; }
        public int ScreenSentFrames { get; init; }
        public int ScreenReceivedFrames { get; init; }
        public double ScreenLatencyP95Ms { get; init; }
        public double ScreenDropRate { get; init; }
        public int ScreenStallCount { get; init; }
        public int TunaFrameCount { get; init; }
        public int TunaDiagnosticFrameCount { get; init; }
        public int TunaLogFrameCount { get; init; }
        public int NknFrameCount { get; init; }
        public bool FallbackExpected { get; init; }
        public bool FallbackStarted { get; init; }
        public bool FallbackFileSent { get; init; }
        public bool FallbackFileReceived { get; init; }
        public bool FallbackScreenSent { get; init; }
        public bool FallbackScreenReceived { get; init; }
        public string TerminalReason { get; init; } = string.Empty;
        public bool ProviderDegraded { get; init; }
        public string FailureReason { get; init; } = string.Empty;
        public string WarningReason { get; set; } = string.Empty;
        public string LogExcerpt { get; set; } = string.Empty;
    }

    private sealed record TunaSoakMatrixSummary(
        string Event,
        string Verdict,
        int CellCount,
        int PassedCells,
        string[] Reasons,
        string[] Warnings,
        TunaSoakCellResult[] Results)
    {
        public static TunaSoakMatrixSummary Build(IEnumerable<TunaSoakCellResult> source)
        {
            var results = source.ToArray();
            var reasons = new List<string>();
            var warnings = new List<string>();
            foreach (var result in results)
            {
                if (!result.Completed || !string.IsNullOrWhiteSpace(result.FailureReason))
                {
                    reasons.Add(result.CellId + ":cell_failed:" + result.FailureReason);
                }

                if (!string.IsNullOrWhiteSpace(result.WarningReason))
                {
                    warnings.Add(result.CellId + ":warning:" + result.WarningReason);
                }

                if (!result.SessionAlive || !result.ChatControlAlive)
                {
                    reasons.Add(result.CellId + ":session_or_control_not_alive");
                }

                if (CellUsesFileTraffic(result) && !result.FileCompleted)
                {
                    reasons.Add(result.CellId + ":file_incomplete");
                }

                if (CellUsesScreenTraffic(result) && !result.ScreenCompleted)
                {
                    reasons.Add(result.CellId + ":screen_incomplete");
                }

                if (result.Transport == Phase3TransportMode.Tuna &&
                    result.Fault == TunaSoakFaultMode.None &&
                    result.TunaFrameCount <= 0)
                {
                    reasons.Add(result.CellId + ":tuna_readiness_missing");
                }

                if (result.FallbackExpected)
                {
                    if (!result.FallbackStarted)
                    {
                        reasons.Add(result.CellId + ":fallback_started_missing");
                    }

                    if (CellUsesFileTraffic(result) &&
                        (!result.FallbackFileSent || !result.FallbackFileReceived))
                    {
                        reasons.Add("fallback_file_proof_missing");
                    }

                    if (CellUsesScreenTraffic(result) &&
                        (!result.FallbackScreenSent || !result.FallbackScreenReceived))
                    {
                        reasons.Add(result.CellId + ":fallback_screen_proof_missing");
                    }
                }
                else if (result.Transport == Phase3TransportMode.Tuna && result.FallbackStarted)
                {
                    if ((CellUsesFileTraffic(result) &&
                         (!result.FallbackFileSent || !result.FallbackFileReceived)) ||
                        (CellUsesScreenTraffic(result) &&
                         (!result.FallbackScreenSent || !result.FallbackScreenReceived)))
                    {
                        reasons.Add(result.CellId + ":unexpected_fallback_proof_missing");
                    }
                    else if (CellUsesFileTraffic(result) &&
                             result.FileReceiveRatio < SoakUnexpectedFallbackRecoveredMinFileRatio)
                    {
                        reasons.Add(result.CellId + ":unexpected_fallback_file_receive_ratio_low");
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(result.TerminalReason))
                        {
                            reasons.Add(result.CellId + ":unexpected_fallback_terminal_reason_missing");
                        }

                        if (!result.WarningReason.Contains("unexpected_tuna_drop_recovered", StringComparison.Ordinal))
                        {
                            warnings.Add(result.CellId + ":warning:unexpected_tuna_drop_recovered:file_receive_ratio=" + result.FileReceiveRatio.ToString("F4", CultureInfo.InvariantCulture));
                        }
                    }
                }
            }

            var verdict = results.Length == 0
                ? "inconclusive"
                : reasons.Count == 0
                    ? "pass"
                    : "fail";
            return new TunaSoakMatrixSummary(
                "soak_matrix_summary",
                verdict,
                results.Length,
                results.Count(static result => result.Completed && string.IsNullOrWhiteSpace(result.FailureReason)),
                reasons.Distinct(StringComparer.Ordinal).ToArray(),
                warnings.Distinct(StringComparer.Ordinal).ToArray(),
                results);
        }
    }

    private enum TunaSoakTier
    {
        Core,
        Extended,
    }

    private enum TunaSoakPreset
    {
        HighQuality,
        TunaQuality,
    }

    private enum TunaSoakTrafficProfile
    {
        ScreenOnly,
        FileOnly,
        MixedScreenFile,
    }

    private enum TunaSoakPayerMode
    {
        None,
        HelpeeOnly,
        HelperOnly,
        BothUnlocked,
    }

    private enum TunaSoakFaultMode
    {
        None,
        AppRestartBeforeTraffic,
        SidecarCrash,
        SwitchOffFallback,
        CapReached,
        ProviderTimeout,
    }
}
