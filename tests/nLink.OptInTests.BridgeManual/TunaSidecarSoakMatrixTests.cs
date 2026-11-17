using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NLink.Core;
using NLink.Core.Configuration;
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
    private const string Phase6ShortMatrixOptInEnv = "NLINK_RUN_TUNA_PHASE6_SHORT_MATRIX";
    private const string Phase6TargetedOptInEnv = "NLINK_RUN_TUNA_PHASE6_TARGETED";
    private const int SoakDefaultDurationMinutes = 15;
    private const int Phase6DefaultDurationMinutes = 5;
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
    public void TunaSoakMatrixSummary_AllowsPhase6ExpectedWaitingWithControlDown()
    {
        var cell = new TunaSoakMatrixCell(
            TunaSoakTier.Core,
            "phase6-tuna-file-helper-receiving-helpee-switch-off",
            Phase3TransportMode.Tuna,
            TunaSoakTrafficProfile.FileOnly,
            TunaSoakPreset.TunaQuality,
            TunaSoakPayerMode.HelpeeOnly,
            TunaSoakFaultMode.SwitchOffFallback)
        {
            ReceiverRole = TunaSoakReceiverRole.HelperReceiving,
        };
        var result = new TunaSoakCellResult
        {
            CellId = cell.CellId,
            Tier = cell.Tier,
            Transport = cell.Transport,
            TrafficProfile = cell.TrafficProfile,
            Preset = cell.Preset,
            Payer = cell.Payer,
            Fault = cell.Fault,
            Completed = false,
            SessionAlive = false,
            ChatControlAlive = false,
            FileCompleted = false,
            ScreenCompleted = true,
            FileBytesSent = 300_000_000,
            FileBytesReceived = 180_000_000,
            FileReceiveRatio = 0.6,
            TunaFrameCount = 10,
            FallbackExpected = true,
            FallbackStarted = true,
            FallbackFileSent = true,
            FallbackFileReceived = true,
            FailureReason = "file_incomplete:receive_ratio=0.6000; reason=waiting_for_regular_nkn",
            IsPhase6Gate = true,
            DataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            V6EpochStarted = true,
            V6TargetProofObserved = true,
            V6EpochWaiting = true,
            V6EpochTerminal = true,
            SenderTerminalObserved = true,
            ReceiverTerminalObserved = true,
            UnresolvedEpochCount = 1,
            ExpectedWaiting = true,
            FinalStatus = "Waiting for regular NKN",
        };

        var summary = TunaSoakMatrixSummary.Build(new[] { result });

        Assert.Equal("pass", summary.Verdict);
        Assert.Empty(summary.Reasons);
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
    public void TunaPhase6RetryAllowsProviderDegradedIncompleteCleanActivation()
    {
        var cleanActivation = new TunaSoakCellResult
        {
            IsPhase6Gate = true,
            Transport = Phase3TransportMode.Tuna,
            Fault = TunaSoakFaultMode.None,
            Completed = false,
            FallbackExpected = false,
            ProviderDegradedAccepted = true,
            ProviderDegradationOverlappedFileTransfer = true,
            FailureReason = "file_incomplete:receive_ratio=0.9222; reason=soak_timeout_incomplete",
            WarningReason = "provider_paths_degraded",
        };
        var expectedFault = new TunaSoakCellResult
        {
            IsPhase6Gate = true,
            Transport = Phase3TransportMode.Tuna,
            Fault = TunaSoakFaultMode.CapReached,
            Completed = false,
            FallbackExpected = true,
            ProviderDegradedAccepted = true,
            ProviderDegradationOverlappedFileTransfer = true,
            FailureReason = "file_incomplete:receive_ratio=0.4662; reason=soak_timeout_incomplete",
            WarningReason = "provider_paths_degraded",
        };
        var providerTimeout = new TunaSoakCellResult
        {
            Completed = false,
            FallbackExpected = false,
            FailureReason = "tuna_readiness_missing:provider_paths_wait_timeout",
        };

        Assert.True(ShouldRetryPhase6SoakCell(cleanActivation, attempt: 1, maxRetries: 1));
        Assert.False(ShouldRetryPhase6SoakCell(expectedFault, attempt: 1, maxRetries: 1));
        Assert.True(ShouldRetryPhase6SoakCell(providerTimeout, attempt: 1, maxRetries: 1));
    }

    [Fact]
    public void TunaSoakSidecarJsonHelpersExtractTerminalAndProviderState()
    {
        var stdout = new ConcurrentQueue<string>();
        stdout.Enqueue("{\"event\":\"ready\"}");
        var start = stdout.Count;
        stdout.Enqueue("{\"event\":\"provider_paths_degraded_accepted\",\"usableCount\":3}");
        stdout.Enqueue("{\"event\":\"provider_paths_recovered\",\"usableCount\":4}");
        stdout.Enqueue("{\"event\":\"tuna_bridge_terminal\",\"terminalReason\":\"tuna_stream_eof\"}");

        var slice = ReadListenerStdoutSlice(stdout, start);

        Assert.Contains("\"event\":\"provider_paths_degraded_accepted\"", slice, StringComparison.Ordinal);
        Assert.Contains("\"event\":\"provider_paths_recovered\"", slice, StringComparison.Ordinal);
        Assert.Equal("tuna_stream_eof", ExtractLastJsonString(slice, "terminalReason"));
    }

    [Fact]
    public void TunaSoakProviderPathDiagnosticsDistinguishRecoveredAndPersistentDegradation()
    {
        var started = new DateTimeOffset(2026, 5, 12, 12, 0, 0, TimeSpan.Zero);
        var recoveredLog = """
            [2026-05-12 12:00:01Z] event=tuna_listener_sidecar_event; sidecar_event=provider_paths_degraded_accepted
            [2026-05-12 12:00:03Z] event=tuna_listener_sidecar_event; sidecar_event=provider_paths_recovered
            """;
        var recoveredStdout = """
            {"event":"provider_paths_degraded_accepted","usableCount":3}
            {"event":"provider_paths_recovered","usableCount":4}
            """;
        var persistentStdout = """
            {"event":"provider_paths_degraded_accepted","usableCount":3}
            {"event":"provider_paths_still_degraded","usableCount":3}
            {"event":"provider_path_quality_summary","qualityClass":"persistent_missing_path","usableCount":3,"missingIndices":[0],"recoveryLatencyMs":-1,"stable3OnlyMs":59000,"finalPathReasons":[{"index":0,"stateReason":"empty_endpoint"},{"index":1,"stateReason":"usable"}]}
            """;
        var lateRecoveredStdout = """
            {"event":"provider_paths_degraded_accepted","usableCount":3}
            {"event":"provider_paths_recovered","usableCount":4}
            """;

        var recovered = AnalyzeProviderPathDiagnostics(recoveredLog, recoveredStdout, started, started.AddMinutes(1));
        var persistent = AnalyzeProviderPathDiagnostics(string.Empty, persistentStdout, started, started.AddMinutes(1));
        var lateRecovered = AnalyzeProviderPathDiagnostics(string.Empty, lateRecoveredStdout, started, started.AddMinutes(1));

        Assert.True(recovered.DegradedAccepted);
        Assert.True(recovered.RecoveredAfterDegraded);
        Assert.False(recovered.StillDegradedAtEnd);
        Assert.Equal(4, recovered.FinalUsableCount);
        Assert.Equal(started.AddSeconds(1), recovered.FirstDegradedUtc);
        Assert.Equal(started.AddSeconds(3), recovered.RecoveredUtc);
        Assert.True(persistent.DegradedAccepted);
        Assert.False(persistent.RecoveredAfterDegraded);
        Assert.True(persistent.StillDegradedAtEnd);
        Assert.Equal(3, persistent.FinalUsableCount);
        Assert.Equal("persistent_missing_path", persistent.QualityClass);
        Assert.Equal([0], persistent.MissingIndices);
        Assert.Equal(59000, persistent.Stable3OnlyMs);
        Assert.Contains("0:empty_endpoint", persistent.FinalPathReasons);
        Assert.True(lateRecovered.DegradedAccepted);
        Assert.False(lateRecovered.RecoveredAfterDegraded);
        Assert.True(lateRecovered.StillDegradedAtEnd);
        Assert.Equal(4, lateRecovered.FinalUsableCount);
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
        var previousDeveloperMode = Environment.GetEnvironmentVariable(ReleaseOverridePolicy.UnsafeDeveloperModeEnvVar);
        var previousNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var previousBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var previousManualBridge = Environment.GetEnvironmentVariable("NLINK_RUN_MANUAL_BRIDGE");
        var results = new List<TunaSoakCellResult>();
        TunaSoakMatrixSummary? summary = null;
        Exception? terminalException = null;
        var matrixStartedAtUtc = DateTimeOffset.UtcNow;

        try
        {
            Environment.SetEnvironmentVariable(ReleaseOverridePolicy.UnsafeDeveloperModeEnvVar, "1");
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
                await WriteProviderQualityReportAsync(artifactDir, summary, CancellationToken.None);
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
            Environment.SetEnvironmentVariable(ReleaseOverridePolicy.UnsafeDeveloperModeEnvVar, previousDeveloperMode);
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
        CancellationToken ct,
        Phase3BenchmarkOptions? phase3OptionsOverride = null,
        bool useV6ServiceFileProfile = false)
    {
        var startedAtUtc = DateTimeOffset.UtcNow;
        var logStart = GetOperationalLogLength();
        var listenerStdoutStart = listenerStdout.Count;
        var phase3Options = phase3OptionsOverride ??
                            options.ToPhase3BenchmarkOptions(
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

            Phase3LiveRunContext? context = null;
            try
            {
                context = await CreateTunaSoakLiveRunContextAsync(
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
                    ct,
                    useV6ServiceFileProfile);
                result.StartedUtc = startedAtUtc;
                result.EndedUtc = DateTimeOffset.UtcNow;
                result.DurationMs = Math.Max(1, (long)(result.EndedUtc - result.StartedUtc).TotalMilliseconds);
                result.LogExcerpt = ExtractTunaSoakProofExcerpt(logStart, startedAtUtc);
                return result;
            }
            finally
            {
                if (context is not null)
                {
                    await DisposeTunaSoakLiveRunContextAsync(
                            context,
                            runsPath,
                            cell.CellId,
                            bounded: useV6ServiceFileProfile,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
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

    private static async Task DisposeTunaSoakLiveRunContextAsync(
        Phase3LiveRunContext context,
        string runsPath,
        string cellId,
        bool bounded,
        CancellationToken ct)
    {
        if (!bounded)
        {
            context.Dispose();
            return;
        }

        context.KillListener();
        var disposeTask = Task.Run(context.Dispose, CancellationToken.None);
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(15), CancellationToken.None);
        var completed = await Task.WhenAny(disposeTask, timeoutTask).ConfigureAwait(false);
        if (completed == disposeTask)
        {
            try
            {
                await disposeTask.ConfigureAwait(false);
                await AppendPhase3EventAsync(
                        runsPath,
                        new { @event = "phase6_context_disposed", cellId, bounded = true, disposedAtUtc = DateTimeOffset.UtcNow },
                        ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await AppendPhase3EventAsync(
                        runsPath,
                        new { @event = "phase6_context_dispose_failed", cellId, error = ex.GetType().Name, disposedAtUtc = DateTimeOffset.UtcNow },
                        ct)
                    .ConfigureAwait(false);
            }

            return;
        }

        await AppendPhase3EventAsync(
                runsPath,
                new { @event = "phase6_context_dispose_timeout", cellId, timeoutMs = 15000, disposedAtUtc = DateTimeOffset.UtcNow },
                ct)
            .ConfigureAwait(false);
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
        CancellationToken ct,
        bool useV6ServiceFileProfile)
    {
        var fileRunId = "soak-file-" + cell.CellId + "-" + Guid.NewGuid().ToString("N")[..8];
        var screenRunId = "soak-screen-" + cell.CellId + "-" + Guid.NewGuid().ToString("N")[..8];
        var faultTask = ScheduleTunaSoakFaultAsync(context, cell, soakOptions, runsPath, ct);
        var fileTask = CellUsesFileTraffic(cell)
            ? useV6ServiceFileProfile
                ? RunPhase6ServiceFileProfileAsync(context, cell, fileRunId, phase3Options, runsPath, logStart, startedAtUtc, ct)
                : RunPhase3FileProfileAsync(context, fileRunId, repeat: 1, phase3Options, logStart, ct, cell.ReceiverRole)
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

        var providerPaths = AnalyzeProviderPathDiagnostics(logTail, sidecarText, startedAtUtc, DateTimeOffset.UtcNow);
        var v6FileEpochPending = CountOccurrences(logTail, "file_v6_epoch_state=pending") > 0;
        var v6EpochStarted = CountOccurrences(logTail, "event=filetransfer_v6_epoch_started") > 0 ||
                             v6FileEpochPending ||
                             CountOccurrences(logTail, "event=tuna_fallback_filetransfer_rebind_requested") > 0;
        var v6TargetProofObserved = CountOccurrences(logTail, "event=filetransfer_v6_transport_probe_ack_sent") > 0 ||
                                    CountOccurrences(logTail, "event=filetransfer_v6_transport_probe_ack_received") > 0 ||
                                    CountOccurrences(logTail, "reason=transport_probe_ack") > 0;
        var v6RepairProofObserved = CountOccurrences(logTail, "event=filetransfer_v6_repair_proof_sent") > 0 ||
                                    CountOccurrences(logTail, "event=filetransfer_v6_repair_proof_received") > 0 ||
                                    CountOccurrences(logTail, "event=filetransfer_v6_frontier_repair_applied") > 0 ||
                                    CountOccurrences(logTail, "reason=frontier_chunk_proof") > 0 ||
                                    CountOccurrences(logTail, "reason=frontier_repair_proof") > 0;
        var v6EpochRecoveredEvent = CountOccurrences(logTail, "event=filetransfer_v6_epoch_recovered") > 0;
        var v6FallbackProofRecovered = CountOccurrences(logTail, "proof=filetransfer_v6_epoch_recovered") > 0;
        var v6LaneRecovered = CountOccurrences(logTail, "file_v6_epoch_state=recovered") > 0;
        var v6EpochRecovered = v6EpochRecoveredEvent || v6FallbackProofRecovered || v6LaneRecovered;
        var v6EpochWaiting = CountOccurrences(logTail, "event=filetransfer_v6_epoch_waiting") > 0 ||
                             CountOccurrences(logTail, "event=filetransfer_fallback_nkn_proof_waiting_for_v6_epoch") > 0 ||
                             CountOccurrences(logTail, "Waiting for regular NKN") > 0 ||
                             (fallbackExpected && v6FileEpochPending && !v6EpochRecovered);
        var v6EpochTerminal = CountOccurrences(logTail, "event=filetransfer_v6_epoch_terminal") > 0;
        var falseRecoveryObserved = HasExplicitV6FalseRecoveryEvidence(logTail);
        var outboundTerminalObserved = CountOccurrences(logTail, "event=file_transfer_outbound_terminal") > 0;
        var inboundTerminalObserved = CountOccurrences(logTail, "event=file_transfer_inbound_terminal") > 0;
        var cancelObserved = CountOccurrences(logTail, "event=filetransfer_v6_cancel_received") > 0 ||
                             CountOccurrences(logTail, "event=filetransfer_v6_cancel_sent") > 0 ||
                             CountOccurrences(logTail, "file_transfer_cancel") > 0;
        var peerCloseObserved = CountOccurrences(logTail, "peer_disconnected") > 0 ||
                                CountOccurrences(logTail, "window_close") > 0 ||
                                CountOccurrences(logTail, "session_end") > 0 ||
                                CountOccurrences(logTail, "app_exit") > 0;
        if (string.IsNullOrWhiteSpace(terminalReason))
        {
            terminalReason = ExtractLastJsonString(sidecarText, "terminalReason");
        }
        var phase6V6FallbackProofComplete =
            IsPhase6GateCell(cell) &&
            v6EpochStarted &&
            (v6EpochRecovered || v6EpochWaiting || v6EpochTerminal) &&
            !falseRecoveryObserved;
        var fallbackProofComplete = fallbackStarted &&
                                    (!CellUsesFileTraffic(cell) || fallbackFileSent && fallbackFileReceived || phase6V6FallbackProofComplete) &&
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
        var expectedWaiting = fallbackExpected &&
                              CellUsesFileTraffic(cell) &&
                              !file.Completed &&
                              file.BytesReceived > 0 &&
                              v6EpochWaiting &&
                              !falseRecoveryObserved;
        var phase6UnexpectedFallbackSafelyRecovered =
            IsPhase6CleanActivationCell(cell) &&
            unexpectedFallbackStarted &&
            file.Completed &&
            file.FinalShaMatched &&
            sessionAlive &&
            phase6V6FallbackProofComplete &&
            (file.SenderTerminalObserved || outboundTerminalObserved) &&
            (file.ReceiverTerminalObserved || inboundTerminalObserved);
        var warnings = new List<string>();
        if (unexpectedFallbackRecovered || phase6UnexpectedFallbackSafelyRecovered)
        {
            warnings.Add(string.Format(CultureInfo.InvariantCulture, "unexpected_tuna_drop_recovered:file_receive_ratio={0:F4}; terminal_reason={1}; file_failure={2}", fileReceiveRatio, string.IsNullOrWhiteSpace(terminalReason) ? "unknown" : terminalReason, file.FailureReason));
        }

        if (cell.Transport == Phase3TransportMode.Tuna && tunaDiagnosticFrames <= 0 && tunaLogFrames > 0)
        {
            warnings.Add(string.Format(CultureInfo.InvariantCulture, "tuna_diagnostic_counter_lost_after_reset:sidecar_forwarded_frames={0}", tunaLogFrames));
        }

        if (cell.Transport == Phase3TransportMode.Tuna && providerPaths.StillDegradedAtEnd)
        {
            warnings.Add("provider_paths_degraded");
        }
        else if (cell.Transport == Phase3TransportMode.Tuna && providerPaths.RecoveredAfterDegraded)
        {
            warnings.Add("provider_paths_degraded_recovered");
        }

        if (IsPhase6CleanActivationCell(cell) && file.Completed && !peerCloseObserved)
        {
            warnings.Add("activation_cleanup_late_peer_close");
            await AppendPhase3EventAsync(
                    runsPath,
                    new
                    {
                        @event = "phase6_activation_cleanup_late_peer_close",
                        cellId = cell.CellId,
                        fileRunId = file.RunId,
                        fileBytesSent = file.BytesSent,
                        fileBytesReceived = file.BytesReceived,
                        senderTerminalObserved = file.SenderTerminalObserved || outboundTerminalObserved,
                        receiverTerminalObserved = file.ReceiverTerminalObserved || inboundTerminalObserved,
                        observedAtUtc = DateTimeOffset.UtcNow,
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
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
        else if (unexpectedFallbackStarted && !fallbackProofComplete && !phase6UnexpectedFallbackSafelyRecovered)
        {
            failureReason = "unexpected_fallback_proof_missing";
        }
        else if (CellUsesFileTraffic(cell) &&
                 unexpectedFallbackStarted &&
                 fileReceiveRatio < SoakUnexpectedFallbackRecoveredMinFileRatio)
        {
            failureReason = string.Format(CultureInfo.InvariantCulture, "unexpected_fallback_file_receive_ratio_low:{0:F4}", fileReceiveRatio);
        }
        else if (unexpectedFallbackStarted && string.IsNullOrWhiteSpace(terminalReason) && !phase6UnexpectedFallbackSafelyRecovered)
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
            ProviderDegradedAccepted = providerPaths.DegradedAccepted,
            ProviderRecoveredAfterDegraded = providerPaths.RecoveredAfterDegraded,
            ProviderStillDegradedAtEnd = providerPaths.StillDegradedAtEnd,
            ProviderFirstDegradedUtc = providerPaths.FirstDegradedUtc,
            ProviderRecoveredUtc = providerPaths.RecoveredUtc,
            ProviderFinalUsableCount = providerPaths.FinalUsableCount,
            ProviderDegradationOverlappedFileTransfer = providerPaths.OverlappedFileTransfer,
            ProviderMissingIndices = providerPaths.MissingIndices,
            ProviderQualityClass = providerPaths.QualityClass,
            ProviderRecoveryLatencyMs = providerPaths.RecoveryLatencyMs,
            ProviderStable3OnlyMs = providerPaths.Stable3OnlyMs,
            ProviderFinalPathReasons = providerPaths.FinalPathReasons,
            FailureReason = failureReason,
            WarningReason = warningReason,
            DataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            V6EpochStarted = v6EpochStarted || v6EpochRecovered || v6EpochWaiting || v6EpochTerminal || file.V6EpochStarted,
            V6TargetProofObserved = v6TargetProofObserved || file.V6TargetProofObserved,
            V6RepairProofObserved = v6RepairProofObserved || file.V6RepairProofObserved,
            V6EpochRecovered = v6EpochRecovered || file.V6EpochRecovered,
            V6EpochWaiting = v6EpochWaiting || file.V6EpochWaiting,
            V6EpochTerminal = v6EpochTerminal || file.V6EpochTerminal,
            FalseRecoveryObserved = falseRecoveryObserved || file.FalseRecoveryObserved,
            SenderTerminalObserved = outboundTerminalObserved || file.SenderTerminalObserved || file.Completed || expectedWaiting,
            ReceiverTerminalObserved = inboundTerminalObserved || file.ReceiverTerminalObserved || file.Completed || expectedWaiting,
            CancelObserved = cancelObserved,
            PeerCloseObserved = peerCloseObserved,
            FinalShaMatched = file.FinalShaMatched || file.Completed,
            SidecarOrphanCount = 0,
            UnresolvedEpochCount = expectedWaiting ? 1 : 0,
            ExpectedWaiting = expectedWaiting,
            FinalStatus = expectedWaiting ? "Waiting for regular NKN" : string.Empty,
        };
    }

    private async Task<Phase3RunResult> RunPhase6ServiceFileProfileAsync(
        Phase3LiveRunContext context,
        TunaSoakMatrixCell cell,
        string runId,
        Phase3BenchmarkOptions options,
        string runsPath,
        int logStart,
        DateTimeOffset startedAtUtc,
        CancellationToken ct)
    {
        var transferId = "phase6-file-" + Guid.NewGuid().ToString("N");
        var payloadBytes = options.FileTargetBytes;
        var seed = unchecked((int)0x6f12d0ab ^ runId.GetHashCode(StringComparison.Ordinal));
        var artifactDir = Path.GetDirectoryName(runsPath) ?? Path.Combine(FindRepoRoot(), "artifacts", "tuna-sidecar");
        var runDir = Path.Combine(artifactDir, "file-runs", SanitizePhase6ArtifactSegment(runId));
        Directory.CreateDirectory(runDir);
        var receivedPath = Path.Combine(runDir, "received.bin");
        var expectedHash = await ComputePhase6DeterministicSha256Base64Async(payloadBytes, seed, ct).ConfigureAwait(false);
        var senderTransport = GetPhase3FileSender(context, cell.ReceiverRole);
        var receiverTransport = GetPhase3FileReceiver(context, cell.ReceiverRole);
        var accelerationAvailableAtStart = IsPhase3TunaLaneReady(context, NknAccelerationLaneKind.File);
        if (context.Mode == Phase3TransportMode.Tuna && !accelerationAvailableAtStart)
        {
            return new Phase3RunResult
            {
                RunId = runId,
                Profile = Phase3Profile.File,
                Mode = context.Mode,
                Repeat = 1,
                FailureReason = "file_tuna_lane_unavailable",
                AccelerationAvailableAtStart = false,
            };
        }

        var senderAccelerationStart = senderTransport.AccelerationDiagnosticsForTests;
        var receiverAccelerationStart = receiverTransport.AccelerationDiagnosticsForTests;
        var senderEpochDiagnosticsStart = senderTransport.FileTransferV6TransportEpochDiagnosticsForTests;
        var receiverEpochDiagnosticsStart = receiverTransport.FileTransferV6TransportEpochDiagnosticsForTests;
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        await AppendPhase3EventAsync(
            runsPath,
            new
            {
                @event = "phase6_service_file_start",
                runId,
                transferId,
                receiverRole = cell.ReceiverRole,
                payloadBytes,
                artifact = Path.GetFileName(runDir),
                startedAtUtc = DateTimeOffset.UtcNow,
            },
            ct).ConfigureAwait(false);

        var started = Stopwatch.StartNew();
        var completed = false;
        var finalShaMatched = false;
        var actualHash = string.Empty;
        var failureReason = string.Empty;
        try
        {
            await sender.TryStartSendAsync(
                    new FileTransferSendDescriptor("phase6-service-file.bin", payloadBytes, transferId),
                    _ => Task.FromResult<Stream>(new Phase6DeterministicPayloadStream(payloadBytes, seed, options.FileSendPacingMbps)),
                    ct)
                .ConfigureAwait(false);

            var offerAccepted = await WaitForPhase6ServiceConditionAsync(
                    () => receiver.Snapshot.Inbound?.TransferId == transferId &&
                          receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision,
                    TimeSpan.FromSeconds(45),
                    ct)
                .ConfigureAwait(false);
            if (!offerAccepted)
            {
                failureReason = "receiver_offer_timeout";
            }
            else
            {
                await receiver.AcceptIncomingTransferAsync(
                        transferId,
                        (_, _) => Task.FromResult<Stream>(new FileStream(
                            receivedPath,
                            FileMode.Create,
                            FileAccess.ReadWrite,
                            FileShare.Read,
                            bufferSize: Math.Clamp(FileTransferChunkBudget.MaxRawChunkBytes, 4096, 64 * 1024),
                            FileOptions.Asynchronous | FileOptions.RandomAccess)),
                        ct)
                    .ConfigureAwait(false);

                var waitCompletedOrWaiting = await WaitForPhase6ServiceTransferCompletionOrWaitingAsync(
                        sender,
                        receiver,
                        transferId,
                        options.ProfileDuration + Phase3FallbackDrainTimeout,
                        IsPhase6FallbackExpected(cell),
                        ct)
                    .ConfigureAwait(false);
                if (!waitCompletedOrWaiting)
                {
                    waitCompletedOrWaiting = await TryConfirmPhase6CleanActivationCompletionAfterDrainAsync(
                            cell,
                            sender,
                            receiver,
                            transferId,
                            receivedPath,
                            payloadBytes,
                            expectedHash,
                            runsPath,
                            runId,
                            ct)
                        .ConfigureAwait(false);
                    if (!waitCompletedOrWaiting)
                    {
                        failureReason = "soak_timeout_incomplete";
                        await ForcePhase6ServiceSoakTimeoutTerminalizationAsync(sender, receiver, transferId).ConfigureAwait(false);
                        await AppendPhase3EventAsync(
                                runsPath,
                                new
                                {
                                    @event = "phase6_activation_cleanup_forced_terminalization",
                                    runId,
                                    transferId,
                                    reason = "soak_timeout_incomplete",
                                    forcedAtUtc = DateTimeOffset.UtcNow,
                                },
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failureReason = ex.GetType().Name + ":" + ex.Message;
        }

        started.Stop();
        var outbound = sender.Snapshot.Outbound;
        var inbound = receiver.Snapshot.Inbound;
        var senderTerminal = outbound?.IsTerminal == true;
        var receiverTerminal = inbound?.IsTerminal == true;
        var waitingForRegularNkn = IsPhase6ServiceWaitingForRegularNkn(outbound) ||
                                   IsPhase6ServiceWaitingForRegularNkn(inbound);
        var receivedBytes = File.Exists(receivedPath) ? new FileInfo(receivedPath).Length : 0L;
        if (File.Exists(receivedPath) && receivedBytes > 0)
        {
            await using var receivedStream = new FileStream(
                receivedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            actualHash = Convert.ToBase64String(await SHA256.HashDataAsync(receivedStream, ct).ConfigureAwait(false));
        }

        completed = outbound?.State == FileTransferTransferState.Completed &&
                    inbound?.State == FileTransferTransferState.Completed &&
                    receivedBytes == payloadBytes;
        finalShaMatched = receivedBytes == payloadBytes &&
                          string.Equals(actualHash, expectedHash, StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(failureReason) && !completed)
        {
            failureReason = waitingForRegularNkn
                ? "waiting_for_regular_nkn"
                : BuildPhase6ServiceFileFailureReason(outbound, inbound, payloadBytes, receivedBytes);
        }
        else if (completed && !finalShaMatched)
        {
            failureReason = "sha_mismatch";
        }

        var accelerationDelta = CreatePhase3AccelerationLaneDelta(
            senderAccelerationStart,
            senderTransport.AccelerationDiagnosticsForTests,
            receiverAccelerationStart,
            receiverTransport.AccelerationDiagnosticsForTests,
            NknBridgeChannel.Bulk);
        var durationMs = Math.Max(1, (long)started.Elapsed.TotalMilliseconds);
        var logTail = ReadTunaSoakOperationalLogSlice(logStart, startedAtUtc);
        var senderEpochDiagnosticsEnd = senderTransport.FileTransferV6TransportEpochDiagnosticsForTests;
        var receiverEpochDiagnosticsEnd = receiverTransport.FileTransferV6TransportEpochDiagnosticsForTests;
        var v6EpochStartedFromDiagnostics =
            senderEpochDiagnosticsEnd.StartedCount > senderEpochDiagnosticsStart.StartedCount ||
            receiverEpochDiagnosticsEnd.StartedCount > receiverEpochDiagnosticsStart.StartedCount;
        var v6TargetProofFromDiagnostics =
            senderEpochDiagnosticsEnd.NormalToTunaActivationRecoveredCount > senderEpochDiagnosticsStart.NormalToTunaActivationRecoveredCount ||
            receiverEpochDiagnosticsEnd.NormalToTunaActivationRecoveredCount > receiverEpochDiagnosticsStart.NormalToTunaActivationRecoveredCount ||
            senderEpochDiagnosticsEnd.RecoveredCount > senderEpochDiagnosticsStart.RecoveredCount ||
            receiverEpochDiagnosticsEnd.RecoveredCount > receiverEpochDiagnosticsStart.RecoveredCount;
        var v6EpochRecoveredFromDiagnostics =
            senderEpochDiagnosticsEnd.RecoveredCount > senderEpochDiagnosticsStart.RecoveredCount ||
            receiverEpochDiagnosticsEnd.RecoveredCount > receiverEpochDiagnosticsStart.RecoveredCount;
        var v6EpochWaitingFromDiagnostics =
            senderEpochDiagnosticsEnd.WaitingCount > senderEpochDiagnosticsStart.WaitingCount ||
            receiverEpochDiagnosticsEnd.WaitingCount > receiverEpochDiagnosticsStart.WaitingCount;
        var v6EpochTerminalFromDiagnostics =
            senderEpochDiagnosticsEnd.TerminalCount > senderEpochDiagnosticsStart.TerminalCount ||
            receiverEpochDiagnosticsEnd.TerminalCount > receiverEpochDiagnosticsStart.TerminalCount;
        var serviceChunkSize = outbound?.ChunkSizeBytes ?? FileTransferChunkBudget.MaxRawChunkBytes;
        var sentFrames = outbound?.ChunksTransferred ??
                         (int)Math.Ceiling(Math.Max(0L, outbound?.BytesAcceptedForTransport ?? 0L) / (double)Math.Max(1, serviceChunkSize));
        var receivedFrames = inbound?.ChunksTransferred ??
                             (int)Math.Ceiling(receivedBytes / (double)Math.Max(1, serviceChunkSize));
        var result = new Phase3RunResult
        {
            RunId = runId,
            Profile = Phase3Profile.File,
            Mode = context.Mode,
            Repeat = 1,
            DurationMs = durationMs,
            BytesSent = payloadBytes,
            BytesReceived = receivedBytes,
            SentFrames = sentFrames,
            ReceivedFrames = receivedFrames,
            SenderThroughputMbps = ToMbps(Math.Max(0L, outbound?.BytesAcceptedForTransport ?? payloadBytes), durationMs),
            ReceiverThroughputMbps = ToMbps(receivedBytes, durationMs),
            Completed = completed && finalShaMatched,
            CapReached = receivedBytes >= payloadBytes,
            StallCount = completed || waitingForRegularNkn ? 0 : 1,
            TunaFrameCount = (int)Math.Min(accelerationDelta.FramesWritten, accelerationDelta.FramesReceived),
            NknFrameCount = Math.Max(0, sentFrames - (int)accelerationDelta.FramesAccepted),
            AccelerationAvailableAtStart = accelerationAvailableAtStart,
            AccelerationFramesAccepted = accelerationDelta.FramesAccepted,
            AccelerationFramesWritten = accelerationDelta.FramesWritten,
            AccelerationFramesReceived = accelerationDelta.FramesReceived,
            AccelerationSendRejected = accelerationDelta.SendRejected,
            AccelerationQueueOverflow = accelerationDelta.QueueOverflow,
            AccelerationLastUnavailableReason = accelerationDelta.LastUnavailableReason,
            FailureReason = (completed && finalShaMatched) ? string.Empty : failureReason,
            FinalShaMatched = finalShaMatched,
            SenderTerminalObserved = senderTerminal,
            ReceiverTerminalObserved = receiverTerminal,
            SenderFinalStatus = outbound?.StatusMessage ?? string.Empty,
            ReceiverFinalStatus = inbound?.StatusMessage ?? string.Empty,
            V6EpochStarted = v6EpochStartedFromDiagnostics ||
                             CountOccurrences(logTail, "event=filetransfer_v6_epoch_started") > 0,
            V6TargetProofObserved = CountOccurrences(logTail, "event=filetransfer_v6_transport_probe_ack_sent") > 0 ||
                                    CountOccurrences(logTail, "event=filetransfer_v6_transport_probe_ack_received") > 0 ||
                                    CountOccurrences(logTail, "reason=transport_probe_ack") > 0 ||
                                    v6TargetProofFromDiagnostics,
            V6RepairProofObserved = CountOccurrences(logTail, "event=filetransfer_v6_repair_proof_sent") > 0 ||
                                    CountOccurrences(logTail, "event=filetransfer_v6_repair_proof_received") > 0 ||
                                    CountOccurrences(logTail, "event=filetransfer_v6_frontier_repair_applied") > 0 ||
                                    CountOccurrences(logTail, "reason=frontier_chunk_proof") > 0 ||
                                    CountOccurrences(logTail, "reason=frontier_repair_proof") > 0,
            V6EpochRecovered = CountOccurrences(logTail, "event=filetransfer_v6_epoch_recovered") > 0 ||
                               CountOccurrences(logTail, "proof=filetransfer_v6_epoch_recovered") > 0 ||
                               CountOccurrences(logTail, "file_v6_epoch_state=recovered") > 0 ||
                               v6EpochRecoveredFromDiagnostics,
            V6EpochWaiting = CountOccurrences(logTail, "event=filetransfer_v6_epoch_waiting") > 0 ||
                             CountOccurrences(logTail, "event=filetransfer_fallback_nkn_proof_waiting_for_v6_epoch") > 0 ||
                             CountOccurrences(logTail, "Waiting for regular NKN") > 0 ||
                             v6EpochWaitingFromDiagnostics,
            V6EpochTerminal = CountOccurrences(logTail, "event=filetransfer_v6_epoch_terminal") > 0 ||
                              v6EpochTerminalFromDiagnostics,
            FalseRecoveryObserved = HasExplicitV6FalseRecoveryEvidence(logTail),
        };

        await AppendPhase3EventAsync(
            runsPath,
            new
            {
                @event = "phase6_service_file_summary",
                runId,
                transferId,
                receiverRole = cell.ReceiverRole,
                completed = result.Completed,
                payloadBytes,
                receivedBytes,
                expectedSha256Base64 = expectedHash,
                actualSha256Base64 = actualHash,
                finalShaMatched,
                senderState = outbound?.State.ToString() ?? "(none)",
                receiverState = inbound?.State.ToString() ?? "(none)",
                senderTerminal,
                receiverTerminal,
                waitingForRegularNkn,
                senderStatus = outbound?.StatusMessage ?? string.Empty,
                receiverStatus = inbound?.StatusMessage ?? string.Empty,
                failureReason = result.FailureReason,
                v6SenderStarted = CountOccurrences(logTail, "event=filetransfer_v6_sender_started") > 0,
                v6ReceiverStarted = CountOccurrences(logTail, "event=filetransfer_v6_receiver_started") > 0,
                v6EpochStartedFromDiagnostics,
                v6TargetProofFromDiagnostics,
                v6EpochRecoveredFromDiagnostics,
                v6EpochWaitingFromDiagnostics,
                v6EpochTerminalFromDiagnostics,
                endedAtUtc = DateTimeOffset.UtcNow,
            },
            CancellationToken.None).ConfigureAwait(false);

        if (!senderTerminal || !receiverTerminal)
        {
            await ForcePhase6ServiceSoakTimeoutTerminalizationAsync(sender, receiver, transferId).ConfigureAwait(false);
            await AppendPhase3EventAsync(
                    runsPath,
                    new
                    {
                        @event = "phase6_activation_cleanup_forced_terminalization",
                        runId,
                        transferId,
                        reason = "terminal_evidence_missing_after_summary",
                        senderTerminalBeforeCleanup = senderTerminal,
                        receiverTerminalBeforeCleanup = receiverTerminal,
                        forcedAtUtc = DateTimeOffset.UtcNow,
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            await AppendPhase3EventAsync(
                    runsPath,
                    new
                    {
                        @event = "phase6_service_file_cleanup_terminalized",
                        runId,
                        transferId,
                        reason = "soak_timeout_incomplete",
                        senderTerminalBeforeCleanup = senderTerminal,
                        receiverTerminalBeforeCleanup = receiverTerminal,
                        cleanupAtUtc = DateTimeOffset.UtcNow,
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        return result;
    }

    private static bool IsPhase6FallbackExpected(TunaSoakMatrixCell cell)
        => cell.Transport == Phase3TransportMode.Tuna &&
           cell.Fault is TunaSoakFaultMode.SidecarCrash or TunaSoakFaultMode.SwitchOffFallback or TunaSoakFaultMode.ProviderTimeout or TunaSoakFaultMode.CapReached;

    private static bool IsPhase6ServiceWaitingForRegularNkn(FileTransferTransferSnapshot? snapshot)
        => string.Equals(snapshot?.StatusMessage, "Waiting for regular NKN", StringComparison.Ordinal);

    private static async Task<bool> TryConfirmPhase6CleanActivationCompletionAfterDrainAsync(
        TunaSoakMatrixCell cell,
        SessionFileTransferService sender,
        SessionFileTransferService receiver,
        string transferId,
        string receivedPath,
        long payloadBytes,
        string expectedHash,
        string runsPath,
        string runId,
        CancellationToken ct)
    {
        if (!IsPhase6CleanActivationCell(cell) || !File.Exists(receivedPath))
        {
            return false;
        }

        var receivedBytes = new FileInfo(receivedPath).Length;
        if (receivedBytes != payloadBytes)
        {
            return false;
        }

        string actualHash;
        await using (var receivedStream = new FileStream(
            receivedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan))
        {
            actualHash = Convert.ToBase64String(await SHA256.HashDataAsync(receivedStream, ct).ConfigureAwait(false));
        }

        if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
        {
            return false;
        }

        var terminalConfirmed = await WaitForPhase6ServiceConditionAsync(
                () =>
                {
                    var outbound = sender.Snapshot.Outbound;
                    var inbound = receiver.Snapshot.Inbound;
                    return string.Equals(outbound?.TransferId, transferId, StringComparison.Ordinal) &&
                           string.Equals(inbound?.TransferId, transferId, StringComparison.Ordinal) &&
                           outbound?.State == FileTransferTransferState.Completed &&
                           inbound?.State == FileTransferTransferState.Completed &&
                           outbound?.IsTerminal == true &&
                           inbound?.IsTerminal == true;
                },
                TimeSpan.FromSeconds(10),
                ct)
            .ConfigureAwait(false);
        if (!terminalConfirmed)
        {
            return false;
        }

        await AppendPhase3EventAsync(
                runsPath,
                new
                {
                    @event = "phase6_activation_cleanup_terminal_confirmed",
                    runId,
                    transferId,
                    payloadBytes,
                    receivedBytes,
                    finalShaMatched = true,
                    drainMs = 10_000,
                    confirmedAtUtc = DateTimeOffset.UtcNow,
                },
                CancellationToken.None)
            .ConfigureAwait(false);
        return true;
    }

    private static async Task<bool> WaitForPhase6ServiceTransferCompletionOrWaitingAsync(
        SessionFileTransferService sender,
        SessionFileTransferService receiver,
        string transferId,
        TimeSpan timeout,
        bool expectedFallback,
        CancellationToken ct)
    {
        var stableWaitingSince = (DateTimeOffset?)null;
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var outbound = sender.Snapshot.Outbound;
            var inbound = receiver.Snapshot.Inbound;
            var terminal = string.Equals(outbound?.TransferId, transferId, StringComparison.Ordinal) &&
                           string.Equals(inbound?.TransferId, transferId, StringComparison.Ordinal) &&
                           outbound?.IsTerminal == true &&
                           inbound?.IsTerminal == true;
            if (terminal)
            {
                return true;
            }

            if (expectedFallback &&
                (IsPhase6ServiceWaitingForRegularNkn(outbound) || IsPhase6ServiceWaitingForRegularNkn(inbound)))
            {
                stableWaitingSince ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - stableWaitingSince.Value >= TimeSpan.FromSeconds(10))
                {
                    return true;
                }
            }
            else
            {
                stableWaitingSince = null;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task ForcePhase6ServiceSoakTimeoutTerminalizationAsync(
        SessionFileTransferService sender,
        SessionFileTransferService receiver,
        string transferId)
    {
        try
        {
            await sender.CancelTransferAsync(transferId, "soak_timeout_incomplete", CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort cleanup for opt-in paid soak evidence; preserve the timeout result.
        }

        try
        {
            await receiver.CancelTransferAsync(transferId, "soak_timeout_incomplete", CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort cleanup for opt-in paid soak evidence; preserve the timeout result.
        }

        await WaitForPhase6ServiceConditionAsync(
                () => sender.Snapshot.Outbound?.IsTerminal == true &&
                      receiver.Snapshot.Inbound?.IsTerminal == true,
                TimeSpan.FromSeconds(5),
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static async Task<bool> WaitForPhase6ServiceConditionAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (condition())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
        }

        return condition();
    }

    private static string BuildPhase6ServiceFileFailureReason(
        FileTransferTransferSnapshot? outbound,
        FileTransferTransferSnapshot? inbound,
        long payloadBytes,
        long receivedBytes)
    {
        var senderState = outbound?.State.ToString() ?? "(none)";
        var receiverState = inbound?.State.ToString() ?? "(none)";
        var senderError = string.IsNullOrWhiteSpace(outbound?.ErrorCode) ? "none" : outbound!.ErrorCode;
        var receiverError = string.IsNullOrWhiteSpace(inbound?.ErrorCode) ? "none" : inbound!.ErrorCode;
        var senderStatus = string.IsNullOrWhiteSpace(outbound?.StatusMessage) ? "none" : outbound!.StatusMessage;
        var receiverStatus = string.IsNullOrWhiteSpace(inbound?.StatusMessage) ? "none" : inbound!.StatusMessage;
        return string.Format(
            CultureInfo.InvariantCulture,
            "service_file_incomplete:sender={0}/{1}/{2}; receiver={3}/{4}/{5}; received={6}/{7}",
            senderState,
            senderError,
            SanitizePhase6FailureToken(senderStatus),
            receiverState,
            receiverError,
            SanitizePhase6FailureToken(receiverStatus),
            receivedBytes,
            payloadBytes);
    }

    private static string SanitizePhase6FailureToken(string value)
        => value.Replace(';', ',').Replace('\r', ' ').Replace('\n', ' ');

    private static string SanitizePhase6ArtifactSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        return builder.Length == 0 ? "run" : builder.ToString();
    }

    private static async Task<string> ComputePhase6DeterministicSha256Base64Async(long length, int seed, CancellationToken ct)
    {
        using var stream = new Phase6DeterministicPayloadStream(length, seed);
        return Convert.ToBase64String(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    private sealed class Phase6DeterministicPayloadStream : Stream
    {
        private readonly long length;
        private readonly int seed;
        private readonly double pacingMbps;
        private readonly Stopwatch pacingStarted = Stopwatch.StartNew();
        private bool pacingEnabled;
        private long position;

        public Phase6DeterministicPayloadStream(long length, int seed, double pacingMbps = 0)
        {
            this.length = Math.Max(0, length);
            this.seed = seed;
            this.pacingMbps = Math.Max(0, pacingMbps);
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => position;
            set
            {
                if (value < 0 || value > length)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                if (value == 0 && position > 0)
                {
                    EnablePacing();
                }

                position = value;
            }
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (position >= length || buffer.Length == 0)
            {
                return 0;
            }

            var bytesToRead = (int)Math.Min(buffer.Length, length - position);
            for (var index = 0; index < bytesToRead; index++)
            {
                buffer[index] = ComputeByte(position + index, seed);
            }

            position += bytesToRead;
            PaceSynchronously(position);
            return bytesToRead;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var bytesRead = ReadWithoutPacing(buffer.Span);
            if (bytesRead > 0)
            {
                await PaceAsync(position, cancellationToken).ConfigureAwait(false);
            }

            return bytesRead;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var bytesRead = ReadWithoutPacing(buffer.AsSpan(offset, count));
            if (bytesRead > 0)
            {
                await PaceAsync(position, cancellationToken).ConfigureAwait(false);
            }

            return bytesRead;
        }

        private int ReadWithoutPacing(Span<byte> buffer)
        {
            if (position >= length || buffer.Length == 0)
            {
                return 0;
            }

            var bytesToRead = (int)Math.Min(buffer.Length, length - position);
            for (var index = 0; index < bytesToRead; index++)
            {
                buffer[index] = ComputeByte(position + index, seed);
            }

            position += bytesToRead;
            return bytesToRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var next = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => position + offset,
                SeekOrigin.End => length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (next < 0 || next > length)
            {
                throw new IOException("Seek position is outside the deterministic payload stream.");
            }

            if (next == 0 && position > 0)
            {
                EnablePacing();
            }

            position = next;
            return position;
        }

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        private void PaceSynchronously(long bytesRead)
        {
            if (!pacingEnabled || pacingMbps <= 0)
            {
                return;
            }

            var targetElapsedMs = bytesRead * 8d / (Math.Max(1, pacingMbps) * 1000d);
            var delayMs = targetElapsedMs - pacingStarted.Elapsed.TotalMilliseconds;
            if (delayMs > 1)
            {
                Task.Delay(TimeSpan.FromMilliseconds(Math.Min(delayMs, 250))).GetAwaiter().GetResult();
            }
        }

        private async Task PaceAsync(long bytesRead, CancellationToken ct)
        {
            if (!pacingEnabled || pacingMbps <= 0)
            {
                return;
            }

            var targetElapsedMs = bytesRead * 8d / (Math.Max(1, pacingMbps) * 1000d);
            var delayMs = targetElapsedMs - pacingStarted.Elapsed.TotalMilliseconds;
            if (delayMs > 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(delayMs, 250)), ct).ConfigureAwait(false);
            }
        }

        private void EnablePacing()
        {
            if (pacingMbps <= 0)
            {
                return;
            }

            pacingEnabled = true;
            pacingStarted.Restart();
        }

        private static byte ComputeByte(long index, int seed)
        {
            unchecked
            {
                var value = (index * 31L) + (seed * 17L) + 113L;
                value %= 251L;
                if (value < 0)
                {
                    value += 251L;
                }

                return (byte)value;
            }
        }
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

        var delay = ResolveTunaSoakFaultDelay(cell, options);
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

    private static bool HasExplicitV6FalseRecoveryEvidence(string logText)
    {
        if (string.IsNullOrWhiteSpace(logText))
        {
            return false;
        }

        var lines = logText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (!line.Contains("filetransfer_v6_epoch_recovered", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.Contains("bridge_ready", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("sidecar_ready", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("send_success", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("send_succeeded", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("bulk_bytes", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("bulk-bytes", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("bridge_frame_forwarded", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("ready_unproven", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("generic", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static ProviderPathSoakDiagnostics AnalyzeProviderPathDiagnostics(
        string logText,
        string sidecarText,
        DateTimeOffset cellStartedUtc,
        DateTimeOffset observedAtUtc)
    {
        var combined = string.Concat(logText, Environment.NewLine, sidecarText);
        var qualitySummary = ReadLastProviderQualitySummary(sidecarText);
        var degradedAccepted =
            ContainsProviderEvent(combined, "provider_paths_degraded_accepted") ||
            ContainsProviderEvent(combined, "provider_paths_degraded");
        var recoveredEventObserved = ContainsProviderEvent(combined, "provider_paths_recovered");
        var firstDegradedUtc = FindFirstLogTimestamp(
            logText,
            "provider_paths_degraded_accepted",
            "provider_paths_degraded") ?? (degradedAccepted ? cellStartedUtc : null);
        var recoveredUtc = FindFirstLogTimestamp(logText, "provider_paths_recovered") ??
                           (recoveredEventObserved ? observedAtUtc : null);
        var recoveredWithRunway = recoveredUtc.HasValue &&
                                  recoveredUtc.Value <= observedAtUtc - TimeSpan.FromSeconds(10);
        var recovered = recoveredEventObserved && recoveredWithRunway;
        var stillDegraded = ContainsProviderEvent(combined, "provider_paths_still_degraded") ||
                            degradedAccepted && (!recoveredEventObserved || !recoveredWithRunway);
        var finalUsableCount =
            qualitySummary?.UsableCount ??
            ExtractLastJsonInt(sidecarText, "usableCount") ??
            ExtractLastLogInt(logText, "usable_provider_count=") ??
            -1;
        var overlapped = degradedAccepted &&
                         firstDegradedUtc <= observedAtUtc &&
                         (!recoveredUtc.HasValue || recoveredUtc.Value >= cellStartedUtc);
        var qualityClass = qualitySummary?.QualityClass ??
                           ClassifyProviderQuality(degradedAccepted, recovered, stillDegraded, finalUsableCount);
        return new ProviderPathSoakDiagnostics(
            degradedAccepted,
            recovered,
            stillDegraded,
            firstDegradedUtc,
            recoveredUtc,
            finalUsableCount,
            overlapped,
            qualityClass,
            qualitySummary?.MissingIndices ?? [],
            qualitySummary?.RecoveryLatencyMs ?? -1,
            qualitySummary?.Stable3OnlyMs ?? -1,
            qualitySummary?.FinalPathReasons ?? []);
    }

    private static string ClassifyProviderQuality(bool degradedAccepted, bool recovered, bool stillDegraded, int finalUsableCount)
    {
        if (!degradedAccepted && finalUsableCount >= 4)
        {
            return "full_ready";
        }

        if (recovered)
        {
            return "degraded_recovered";
        }

        if (stillDegraded)
        {
            return "persistent_missing_path";
        }

        if (!degradedAccepted && finalUsableCount >= 0 && finalUsableCount < 3)
        {
            return "timeout_before_degraded";
        }

        return "unknown";
    }

    private static bool ContainsProviderEvent(string text, string eventName)
        => CountOccurrences(text, "sidecar_event=" + eventName) > 0 ||
           CountOccurrences(text, "\"event\":\"" + eventName + "\"") > 0 ||
           CountOccurrences(text, "event=" + eventName) > 0;

    private static DateTimeOffset? FindFirstLogTimestamp(string logText, params string[] tokens)
    {
        if (string.IsNullOrWhiteSpace(logText))
        {
            return null;
        }

        foreach (var line in logText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!tokens.Any(token => line.Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (TryReadOperationalLogTimestamp(line, out var timestamp))
            {
                return timestamp;
            }
        }

        return null;
    }

    private static int? ExtractLastJsonInt(string text, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(propertyName))
        {
            return null;
        }

        var prefix = "\"" + propertyName + "\":";
        var index = text.LastIndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var start = index + prefix.Length;
        while (start < text.Length && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        var end = start;
        while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '-'))
        {
            end++;
        }

        return int.TryParse(text[start..end], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static ProviderPathQualitySummary? ReadLastProviderQualitySummary(string sidecarText)
    {
        if (string.IsNullOrWhiteSpace(sidecarText))
        {
            return null;
        }

        ProviderPathQualitySummary? summary = null;
        foreach (var line in sidecarText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("event", out var eventName) ||
                    eventName.ValueKind != JsonValueKind.String ||
                    !string.Equals(eventName.GetString(), "provider_path_quality_summary", StringComparison.Ordinal))
                {
                    continue;
                }

                summary = new ProviderPathQualitySummary(
                    root.TryGetProperty("qualityClass", out var qualityClass) && qualityClass.ValueKind == JsonValueKind.String
                        ? qualityClass.GetString() ?? "unknown"
                        : "unknown",
                    root.TryGetProperty("usableCount", out var usableCount) && usableCount.TryGetInt32(out var parsedUsable)
                        ? parsedUsable
                        : -1,
                    ReadJsonIntArray(root, "missingIndices"),
                    root.TryGetProperty("recoveryLatencyMs", out var recoveryLatency) && recoveryLatency.TryGetInt64(out var parsedRecovery)
                        ? parsedRecovery
                        : -1,
                    root.TryGetProperty("stable3OnlyMs", out var stable3Only) && stable3Only.TryGetInt64(out var parsedStable3Only)
                        ? parsedStable3Only
                        : -1,
                    ReadJsonFinalPathReasons(root));
            }
            catch (JsonException)
            {
                // Ignore unrelated sidecar lines.
            }
        }

        return summary;
    }

    private static int[] ReadJsonIntArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<int>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.TryGetInt32(out var value))
            {
                values.Add(value);
            }
        }

        return values.ToArray();
    }

    private static string[] ReadJsonFinalPathReasons(JsonElement root)
    {
        if (!root.TryGetProperty("finalPathReasons", out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var index = item.TryGetProperty("index", out var indexElement) &&
                        indexElement.TryGetInt32(out var parsedIndex)
                ? parsedIndex
                : -1;
            var reason = item.TryGetProperty("stateReason", out var reasonElement) &&
                         reasonElement.ValueKind == JsonValueKind.String
                ? reasonElement.GetString()
                : "unknown";
            values.Add(index.ToString(CultureInfo.InvariantCulture) + ":" + (reason ?? "unknown"));
        }

        return values.ToArray();
    }

    private static int? ExtractLastLogInt(string text, string prefix)
    {
        var token = ExtractLastLogToken(text, prefix);
        return int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
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

    private static bool ShouldRetryPhase6SoakCell(TunaSoakCellResult result, int attempt, int maxRetries)
    {
        if (attempt > maxRetries ||
            result.Completed ||
            result.FallbackExpected)
        {
            return false;
        }

        var retryEvidence = string.Concat(result.FailureReason, ";", result.WarningReason);
        if (result.IsPhase6Gate &&
            result.Transport == Phase3TransportMode.Tuna &&
            result.Fault == TunaSoakFaultMode.None &&
            result.ProviderDegradedAccepted &&
            result.ProviderDegradationOverlappedFileTransfer &&
            result.FailureReason.Contains("soak_timeout_incomplete", StringComparison.OrdinalIgnoreCase) &&
            result.FailureReason.Contains("file_incomplete", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return retryEvidence.Contains("file_tuna_lane_unavailable", StringComparison.OrdinalIgnoreCase) ||
               retryEvidence.Contains("tuna_readiness_missing", StringComparison.OrdinalIgnoreCase) ||
               retryEvidence.Contains("provider_paths_wait_timeout", StringComparison.OrdinalIgnoreCase) ||
               retryEvidence.Contains("tuna_provider_paths_ready_timeout", StringComparison.OrdinalIgnoreCase) ||
               retryEvidence.Contains("dialer_exited", StringComparison.OrdinalIgnoreCase) ||
               retryEvidence.Contains("listener_ready", StringComparison.OrdinalIgnoreCase);
    }

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

    private static bool IsPhase6GateCell(TunaSoakMatrixCell cell)
        => cell.CellId.StartsWith("phase6-tuna-file-", StringComparison.OrdinalIgnoreCase);

    private static bool IsPhase6CleanActivationCell(TunaSoakMatrixCell cell)
        => IsPhase6GateCell(cell) &&
           cell.Transport == Phase3TransportMode.Tuna &&
           cell.Fault == TunaSoakFaultMode.None &&
           CellUsesFileTraffic(cell);

    private static TimeSpan ResolveTunaSoakFaultDelay(TunaSoakMatrixCell cell, TunaSoakMatrixOptions options)
    {
        if (IsPhase6GateCell(cell) &&
            cell.Fault is TunaSoakFaultMode.SwitchOffFallback or TunaSoakFaultMode.SidecarCrash)
        {
            return TimeSpan.FromSeconds(30);
        }

        return cell.Fault == TunaSoakFaultMode.ProviderTimeout
            ? TimeSpan.FromSeconds(Math.Max(45, Math.Min(options.CellDuration.TotalSeconds / 2, 150)))
            : TimeSpan.FromSeconds(Math.Max(20, Math.Min(options.CellDuration.TotalSeconds / 3, 90)));
    }

    private static bool HasFileFallbackProof(TunaSoakCellResult result)
        => result.FallbackFileSent &&
           result.FallbackFileReceived ||
           HasPhase6V6FallbackProof(result);

    private static bool HasPhase6V6FallbackProof(TunaSoakCellResult result)
        => result.IsPhase6Gate &&
           result.DataProtocolVersion == FileTransferProtocol.ProtocolVersionV6 &&
           result.V6EpochStarted &&
           (result.V6EpochRecovered || result.V6EpochWaiting || result.V6EpochTerminal) &&
           !result.FalseRecoveryObserved;

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
        Phase6ShortMatrixOptInEnv,
        Phase6TargetedOptInEnv,
        SoakDurationMinutesEnv,
        SoakNetworkLabelEnv,
        SoakNetworkPairIdEnv,
        SoakTiersEnv,
        SoakCellFilterEnv,
        SoakFilePacingMbpsEnv,
        SoakCellRetriesEnv,
        TunaTestDegradedProviderGraceSecondsEnv,
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
        public TunaSoakReceiverRole ReceiverRole { get; init; } = TunaSoakReceiverRole.HelpeeReceiving;

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
        public bool ProviderDegradedAccepted { get; set; }
        public bool ProviderRecoveredAfterDegraded { get; set; }
        public bool ProviderStillDegradedAtEnd { get; set; }
        public DateTimeOffset? ProviderFirstDegradedUtc { get; set; }
        public DateTimeOffset? ProviderRecoveredUtc { get; set; }
        public int ProviderFinalUsableCount { get; set; }
        public bool ProviderDegradationOverlappedFileTransfer { get; set; }
        public int[] ProviderMissingIndices { get; set; } = [];
        public string ProviderQualityClass { get; set; } = "unknown";
        public long ProviderRecoveryLatencyMs { get; set; } = -1;
        public long ProviderStable3OnlyMs { get; set; } = -1;
        public string[] ProviderFinalPathReasons { get; set; } = [];
        public string FailureReason { get; init; } = string.Empty;
        public string WarningReason { get; set; } = string.Empty;
        public string LogExcerpt { get; set; } = string.Empty;
        public bool IsPhase6Gate { get; set; }
        public int DataProtocolVersion { get; set; }
        public bool V6EpochStarted { get; set; }
        public bool V6TargetProofObserved { get; set; }
        public bool V6RepairProofObserved { get; set; }
        public bool V6EpochRecovered { get; set; }
        public bool V6EpochWaiting { get; set; }
        public bool V6EpochTerminal { get; set; }
        public bool FalseRecoveryObserved { get; set; }
        public bool SenderTerminalObserved { get; set; }
        public bool ReceiverTerminalObserved { get; set; }
        public bool CancelObserved { get; set; }
        public bool PeerCloseObserved { get; set; }
        public bool FinalShaMatched { get; set; }
        public int SidecarOrphanCount { get; set; }
        public int UnresolvedEpochCount { get; set; }
        public bool ExpectedWaiting { get; set; }
        public string FinalStatus { get; set; } = string.Empty;
    }

    private sealed record ProviderPathSoakDiagnostics(
        bool DegradedAccepted,
        bool RecoveredAfterDegraded,
        bool StillDegradedAtEnd,
        DateTimeOffset? FirstDegradedUtc,
        DateTimeOffset? RecoveredUtc,
        int FinalUsableCount,
        bool OverlappedFileTransfer,
        string QualityClass,
        int[] MissingIndices,
        long RecoveryLatencyMs,
        long Stable3OnlyMs,
        string[] FinalPathReasons);

    private sealed record ProviderPathQualitySummary(
        string QualityClass,
        int UsableCount,
        int[] MissingIndices,
        long RecoveryLatencyMs,
        long Stable3OnlyMs,
        string[] FinalPathReasons);

    private sealed record TunaSoakMatrixSummary(
        string Event,
        string Verdict,
        int CellCount,
        int PassedCells,
        string[] Reasons,
        string[] Warnings,
        int ProviderDegradedAcceptedCells,
        int ProviderRecoveredAfterDegradedCells,
        int ProviderStillDegradedAtEndCells,
        TunaSoakCellResult[] Results)
    {
        public static TunaSoakMatrixSummary Build(IEnumerable<TunaSoakCellResult> source)
        {
            var results = source.ToArray();
            var reasons = new List<string>();
            var warnings = new List<string>();
            foreach (var result in results)
            {
                var phase6ExpectedWaiting = IsPhase6ExpectedWaiting(result);
                var phase6ExpectedTerminal = IsPhase6ExpectedTerminal(result);
                var phase6ExpectedNonCompletedOutcome = phase6ExpectedWaiting || phase6ExpectedTerminal;
                if (!result.Completed || !string.IsNullOrWhiteSpace(result.FailureReason))
                {
                    if (!phase6ExpectedNonCompletedOutcome)
                    {
                        reasons.Add(result.CellId + ":cell_failed:" + result.FailureReason);
                    }
                }

                if (!string.IsNullOrWhiteSpace(result.WarningReason))
                {
                    warnings.Add(result.CellId + ":warning:" + result.WarningReason);
                }

                if ((!result.SessionAlive || !result.ChatControlAlive) && !phase6ExpectedNonCompletedOutcome)
                {
                    reasons.Add(result.CellId + ":session_or_control_not_alive");
                }

                if (CellUsesFileTraffic(result) && !result.FileCompleted && !phase6ExpectedNonCompletedOutcome)
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
                    if (!result.FallbackStarted && !phase6ExpectedNonCompletedOutcome)
                    {
                        reasons.Add(result.CellId + ":fallback_started_missing");
                    }

                    if (CellUsesFileTraffic(result) &&
                        !HasFileFallbackProof(result) &&
                        !phase6ExpectedNonCompletedOutcome)
                    {
                        reasons.Add(result.CellId + ":fallback_file_proof_missing");
                    }

                    if (CellUsesScreenTraffic(result) &&
                        (!result.FallbackScreenSent || !result.FallbackScreenReceived))
                    {
                        reasons.Add(result.CellId + ":fallback_screen_proof_missing");
                    }
                }
                else if (result.Transport == Phase3TransportMode.Tuna && result.FallbackStarted)
                {
                    var phase6UnexpectedFallbackSafelyRecovered = IsPhase6UnexpectedFallbackSafelyRecovered(result);
                    if ((CellUsesFileTraffic(result) &&
                         !HasFileFallbackProof(result)) ||
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
                        if (string.IsNullOrWhiteSpace(result.TerminalReason) &&
                            !phase6UnexpectedFallbackSafelyRecovered)
                        {
                            reasons.Add(result.CellId + ":unexpected_fallback_terminal_reason_missing");
                        }

                        if (!result.WarningReason.Contains("unexpected_tuna_drop_recovered", StringComparison.Ordinal))
                        {
                            warnings.Add(result.CellId + ":warning:unexpected_tuna_drop_recovered:file_receive_ratio=" + result.FileReceiveRatio.ToString("F4", CultureInfo.InvariantCulture));
                        }
                    }
                }

                if (result.IsPhase6Gate)
                {
                    AddPhase6GateReasons(result, phase6ExpectedWaiting, phase6ExpectedTerminal, reasons);
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
                results.Count(static result =>
                    IsPhase6ExpectedWaiting(result) ||
                    IsPhase6ExpectedTerminal(result) ||
                    (result.Completed && string.IsNullOrWhiteSpace(result.FailureReason))),
                reasons.Distinct(StringComparer.Ordinal).ToArray(),
                warnings.Distinct(StringComparer.Ordinal).ToArray(),
                results.Count(static result => result.ProviderDegradedAccepted),
                results.Count(static result => result.ProviderRecoveredAfterDegraded),
                results.Count(static result => result.ProviderStillDegradedAtEnd),
                results);
        }

        private static bool IsPhase6ExpectedWaiting(TunaSoakCellResult result)
            => result.IsPhase6Gate &&
               result.ExpectedWaiting &&
               result.V6EpochWaiting &&
               !result.FalseRecoveryObserved &&
               string.Equals(result.FinalStatus, "Waiting for regular NKN", StringComparison.Ordinal);

        private static bool IsPhase6ExpectedTerminal(TunaSoakCellResult result)
            => result.IsPhase6Gate &&
               result.FallbackExpected &&
               CellUsesFileTraffic(result) &&
               !result.FileCompleted &&
               result.DataProtocolVersion == FileTransferProtocol.ProtocolVersionV6 &&
               result.FallbackStarted &&
               HasFileFallbackProof(result) &&
               result.V6EpochStarted &&
               (result.V6EpochRecovered || result.V6EpochTerminal) &&
               !result.FalseRecoveryObserved &&
               result.SidecarOrphanCount == 0 &&
               result.UnresolvedEpochCount == 0 &&
               result.SenderTerminalObserved &&
               result.ReceiverTerminalObserved &&
               result.FinalStatus is not ("Sending..." or "Receiving...") &&
               IsPhase6ExpectedTerminalFailureReason(result.FailureReason);

        private static bool IsPhase6ExpectedTerminalFailureReason(string failureReason)
            => failureReason.Contains("soak_timeout_incomplete", StringComparison.OrdinalIgnoreCase) ||
               failureReason.Contains(FileTransferResultCodes.PeerDisconnected, StringComparison.OrdinalIgnoreCase) ||
               failureReason.Contains(FileTransferResultCodes.TransportDisconnected, StringComparison.OrdinalIgnoreCase);

        private static bool IsPhase6UnexpectedFallbackSafelyRecovered(TunaSoakCellResult result)
            => result.IsPhase6Gate &&
               result.Transport == Phase3TransportMode.Tuna &&
               result.Fault == TunaSoakFaultMode.None &&
               result.FallbackStarted &&
               result.Completed &&
               result.FileCompleted &&
               result.FinalShaMatched &&
               result.SessionAlive &&
               result.ChatControlAlive &&
               HasPhase6V6FallbackProof(result) &&
               result.SidecarOrphanCount == 0 &&
               result.UnresolvedEpochCount == 0 &&
               result.SenderTerminalObserved &&
               result.ReceiverTerminalObserved &&
               result.FinalStatus is not ("Sending..." or "Receiving...");

        private static void AddPhase6GateReasons(
            TunaSoakCellResult result,
            bool expectedWaiting,
            bool expectedTerminal,
            List<string> reasons)
        {
            var expectedNonCompletedOutcome = expectedWaiting || expectedTerminal;
            if (result.DataProtocolVersion != FileTransferProtocol.ProtocolVersionV6)
            {
                reasons.Add(result.CellId + ":data_protocol_not_v6");
            }

            if (CellUsesFileTraffic(result) && !expectedNonCompletedOutcome && !result.FinalShaMatched)
            {
                reasons.Add(result.CellId + ":final_sha_missing_or_mismatch");
            }

            if (result.FalseRecoveryObserved)
            {
                reasons.Add(result.CellId + ":false_recovery");
            }

            if (result.SidecarOrphanCount > 0)
            {
                reasons.Add(result.CellId + ":sidecar_orphaned");
            }

            if (result.UnresolvedEpochCount > 0 && !expectedNonCompletedOutcome)
            {
                reasons.Add(result.CellId + ":unresolved_v6_epoch");
            }

            if (!result.SenderTerminalObserved && !expectedNonCompletedOutcome)
            {
                reasons.Add(result.CellId + ":sender_terminal_missing");
            }

            if (!result.ReceiverTerminalObserved && !expectedNonCompletedOutcome)
            {
                reasons.Add(result.CellId + ":receiver_terminal_missing");
            }

            if (result.FinalStatus is "Sending..." or "Receiving...")
            {
                reasons.Add(result.CellId + ":zombie_transfer_status");
            }

            if (result.FallbackExpected &&
                !expectedNonCompletedOutcome &&
                !result.V6EpochRecovered &&
                !result.V6EpochTerminal)
            {
                reasons.Add(result.CellId + ":v6_epoch_recovery_or_terminal_missing");
            }

            if (result.FallbackExpected && !result.V6EpochStarted)
            {
                reasons.Add(result.CellId + ":v6_epoch_start_missing");
            }
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

    private enum TunaSoakReceiverRole
    {
        HelpeeReceiving,
        HelperReceiving,
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
