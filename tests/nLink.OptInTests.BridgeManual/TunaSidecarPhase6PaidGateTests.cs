using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using NLink.Core.Configuration;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class TunaSidecarLiveManualTests
{
    private const int Phase6FileWriteBytes = 20 * 1024;

    [Fact]
    public void TunaSidecarPhase6ShortMatrix_BuildsExactlyRequiredCells()
    {
        var snapshot = CaptureSoakEnvironment();
        try
        {
            ClearSoakEnvironment();

            var options = LoadPhase6ShortMatrixOptions();
            var cells = BuildPhase6ShortMatrixCells(options);

            Assert.Equal(TimeSpan.FromMinutes(Phase6DefaultDurationMinutes), options.CellDuration);
            Assert.Equal(SoakDefaultFilePacingMbps, options.FileSendPacingMbps);
            Assert.Equal(1, options.MaxCellRetries);
            Assert.Equal(12, cells.Count);
            Assert.Equal(12, cells.Select(static cell => cell.CellId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(cells, static cell =>
            {
                Assert.Equal(Phase3TransportMode.Tuna, cell.Transport);
                Assert.Equal(TunaSoakTrafficProfile.FileOnly, cell.TrafficProfile);
                Assert.Equal(TunaSoakPreset.TunaQuality, cell.Preset);
            });
            Assert.Equal(6, cells.Count(static cell => cell.ReceiverRole == TunaSoakReceiverRole.HelperReceiving));
            Assert.Equal(6, cells.Count(static cell => cell.ReceiverRole == TunaSoakReceiverRole.HelpeeReceiving));
            Assert.Equal(4, cells.Count(static cell => cell.Payer == TunaSoakPayerMode.HelpeeOnly));
            Assert.Equal(4, cells.Count(static cell => cell.Payer == TunaSoakPayerMode.HelperOnly));
            Assert.Equal(4, cells.Count(static cell => cell.Payer == TunaSoakPayerMode.BothUnlocked));
            Assert.Equal(6, cells.Count(static cell => cell.Fault == TunaSoakFaultMode.None));
            Assert.Equal(2, cells.Count(static cell => cell.Fault == TunaSoakFaultMode.SwitchOffFallback));
            Assert.Equal(2, cells.Count(static cell => cell.Fault == TunaSoakFaultMode.CapReached));
            Assert.Equal(2, cells.Count(static cell => cell.Fault == TunaSoakFaultMode.SidecarCrash));
        }
        finally
        {
            RestoreSoakEnvironment(snapshot);
        }
    }

    [Fact]
    public void TunaSidecarPhase6ArtifactPathValidation_RejectsOutsideRepoArtifacts()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), "nlink-phase6-artifact-root-" + Guid.NewGuid().ToString("N"));
        var inside = ResolvePhase6ArtifactDirectoryForTests(repoRoot, Path.Combine(repoRoot, "artifacts", "tuna-sidecar", "phase6-short-test"));

        Assert.StartsWith(Path.Combine(repoRoot, "artifacts"), inside, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidOperationException>(() => ResolvePhase6ArtifactDirectoryForTests(repoRoot, Path.Combine(Path.GetTempPath(), "outside-phase6")));
        Assert.Throws<InvalidOperationException>(() => ResolvePhase6ArtifactDirectoryForTests(repoRoot, Path.Combine(repoRoot, "Downloads", "phase6")));
    }

    [Fact]
    public void TunaSidecarPhase6Summary_FailsBlockingConditions()
    {
        var result = new TunaSoakCellResult
        {
            CellId = "phase6-blockers",
            Tier = TunaSoakTier.Core,
            Transport = Phase3TransportMode.Tuna,
            TrafficProfile = TunaSoakTrafficProfile.FileOnly,
            Preset = TunaSoakPreset.TunaQuality,
            Payer = TunaSoakPayerMode.HelpeeOnly,
            Fault = TunaSoakFaultMode.None,
            Completed = true,
            SessionAlive = true,
            ChatControlAlive = true,
            FileCompleted = true,
            ScreenCompleted = true,
            FileBytesSent = 1024,
            FileBytesReceived = 1024,
            FileReceiveRatio = 1,
            TunaFrameCount = 1,
            IsPhase6Gate = true,
            DataProtocolVersion = 5,
            FinalShaMatched = false,
            FalseRecoveryObserved = true,
            SenderTerminalObserved = false,
            ReceiverTerminalObserved = false,
            SidecarOrphanCount = 1,
            UnresolvedEpochCount = 1,
            FinalStatus = "Sending...",
        };

        var summary = TunaSoakMatrixSummary.Build(new[] { result });

        Assert.Equal("fail", summary.Verdict);
        Assert.Contains("phase6-blockers:data_protocol_not_v6", summary.Reasons);
        Assert.Contains("phase6-blockers:final_sha_missing_or_mismatch", summary.Reasons);
        Assert.Contains("phase6-blockers:false_recovery", summary.Reasons);
        Assert.Contains("phase6-blockers:sender_terminal_missing", summary.Reasons);
        Assert.Contains("phase6-blockers:receiver_terminal_missing", summary.Reasons);
        Assert.Contains("phase6-blockers:sidecar_orphaned", summary.Reasons);
        Assert.Contains("phase6-blockers:unresolved_v6_epoch", summary.Reasons);
        Assert.Contains("phase6-blockers:zombie_transfer_status", summary.Reasons);
    }

    [Fact]
    public void TunaSidecarPhase6Summary_AcceptsExpectedWaitingWithV6Evidence()
    {
        var result = new TunaSoakCellResult
        {
            CellId = "phase6-waiting",
            Tier = TunaSoakTier.Core,
            Transport = Phase3TransportMode.Tuna,
            TrafficProfile = TunaSoakTrafficProfile.FileOnly,
            Preset = TunaSoakPreset.TunaQuality,
            Payer = TunaSoakPayerMode.HelpeeOnly,
            Fault = TunaSoakFaultMode.SwitchOffFallback,
            SessionAlive = true,
            ChatControlAlive = true,
            ScreenCompleted = true,
            FileBytesSent = 1024,
            FileBytesReceived = 512,
            FileReceiveRatio = 0.5,
            TunaFrameCount = 1,
            IsPhase6Gate = true,
            DataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            Completed = false,
            FileCompleted = false,
            FinalShaMatched = false,
            FallbackExpected = true,
            FallbackStarted = true,
            FallbackFileSent = false,
            FallbackFileReceived = false,
            V6EpochStarted = true,
            V6EpochWaiting = true,
            UnresolvedEpochCount = 1,
            ExpectedWaiting = true,
            FinalStatus = "Waiting for regular NKN",
            FailureReason = "waiting_for_regular_nkn",
        };

        var summary = TunaSoakMatrixSummary.Build(new[] { result });

        Assert.Equal("pass", summary.Verdict);
        Assert.Equal(1, summary.PassedCells);
        Assert.Empty(summary.Reasons);
    }

    [Fact]
    public void TunaSidecarPhase6Summary_AcceptsExpectedTerminalFaultWithV6Evidence()
    {
        var result = new TunaSoakCellResult
        {
            CellId = "phase6-terminal-fault",
            Tier = TunaSoakTier.Core,
            Transport = Phase3TransportMode.Tuna,
            TrafficProfile = TunaSoakTrafficProfile.FileOnly,
            Preset = TunaSoakPreset.TunaQuality,
            Payer = TunaSoakPayerMode.HelperOnly,
            Fault = TunaSoakFaultMode.CapReached,
            SessionAlive = true,
            ChatControlAlive = true,
            ScreenCompleted = true,
            FileBytesSent = 300_000_000,
            FileBytesReceived = 190_000_000,
            FileReceiveRatio = 0.63,
            TunaFrameCount = 1000,
            IsPhase6Gate = true,
            DataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            Completed = false,
            FileCompleted = false,
            FinalShaMatched = false,
            FallbackExpected = true,
            FallbackStarted = true,
            FallbackFileSent = true,
            FallbackFileReceived = true,
            V6EpochStarted = true,
            V6EpochRecovered = true,
            SenderTerminalObserved = true,
            ReceiverTerminalObserved = true,
            FailureReason = "file_incomplete:receive_ratio=0.6333; reason=soak_timeout_incomplete",
        };

        var summary = TunaSoakMatrixSummary.Build(new[] { result });

        Assert.Equal("pass", summary.Verdict);
        Assert.Equal(1, summary.PassedCells);
        Assert.Empty(summary.Reasons);
    }

    [Fact]
    public void TunaSidecarPhase6Summary_AcceptsCompletedFallbackWithV6ProofOnly()
    {
        var result = new TunaSoakCellResult
        {
            CellId = "phase6-completed-v6-proof-only",
            Tier = TunaSoakTier.Core,
            Transport = Phase3TransportMode.Tuna,
            TrafficProfile = TunaSoakTrafficProfile.FileOnly,
            Preset = TunaSoakPreset.TunaQuality,
            Payer = TunaSoakPayerMode.HelpeeOnly,
            Fault = TunaSoakFaultMode.SwitchOffFallback,
            Completed = true,
            SessionAlive = true,
            ChatControlAlive = true,
            FileCompleted = true,
            ScreenCompleted = true,
            FileBytesSent = 300_000_000,
            FileBytesReceived = 300_000_000,
            FileReceiveRatio = 1,
            TunaFrameCount = 1000,
            IsPhase6Gate = true,
            DataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            FinalShaMatched = true,
            FallbackExpected = true,
            FallbackStarted = true,
            FallbackFileSent = false,
            FallbackFileReceived = false,
            V6EpochStarted = true,
            V6EpochRecovered = true,
            SenderTerminalObserved = true,
            ReceiverTerminalObserved = true,
        };

        var summary = TunaSoakMatrixSummary.Build(new[] { result });

        Assert.Equal("pass", summary.Verdict);
        Assert.Empty(summary.Reasons);
    }

    [Fact]
    public void TunaSidecarPhase6Summary_DistinguishesProviderPathRecoveryWarnings()
    {
        var recovered = CreatePassingPhase6Result("phase6-provider-recovered");
        recovered.WarningReason = "provider_paths_degraded_recovered";
        recovered.ProviderDegradedAccepted = true;
        recovered.ProviderRecoveredAfterDegraded = true;
        recovered.ProviderStillDegradedAtEnd = false;
        recovered.ProviderFinalUsableCount = 4;
        var persistent = CreatePassingPhase6Result("phase6-provider-persistent");
        persistent.WarningReason = "provider_paths_degraded";
        persistent.ProviderDegradedAccepted = true;
        persistent.ProviderRecoveredAfterDegraded = false;
        persistent.ProviderStillDegradedAtEnd = true;
        persistent.ProviderFinalUsableCount = 3;

        var summary = TunaSoakMatrixSummary.Build(new[] { recovered, persistent });

        Assert.Equal("pass", summary.Verdict);
        Assert.Equal(2, summary.ProviderDegradedAcceptedCells);
        Assert.Equal(1, summary.ProviderRecoveredAfterDegradedCells);
        Assert.Equal(1, summary.ProviderStillDegradedAtEndCells);
        Assert.Contains("phase6-provider-recovered:warning:provider_paths_degraded_recovered", summary.Warnings);
        Assert.Contains("phase6-provider-persistent:warning:provider_paths_degraded", summary.Warnings);
    }

    [Fact]
    public void TunaSidecarPhase6Summary_AcceptsCleanActivationWithoutPeerCloseEvidence()
    {
        var result = CreatePassingPhase6Result("phase6-clean-activation-no-peer-close");
        result.PeerCloseObserved = false;
        result.WarningReason = "activation_cleanup_late_peer_close";

        var summary = TunaSoakMatrixSummary.Build(new[] { result });

        Assert.Equal("pass", summary.Verdict);
        Assert.Empty(summary.Reasons);
        Assert.Contains("phase6-clean-activation-no-peer-close:warning:activation_cleanup_late_peer_close", summary.Warnings);
    }

    [Fact]
    public void TunaSidecarPhase6Summary_AcceptsCleanActivationWithV6UnexpectedFallbackProofOnly()
    {
        var result = new TunaSoakCellResult
        {
            CellId = "phase6-clean-activation-unexpected-fallback",
            Tier = TunaSoakTier.Core,
            Transport = Phase3TransportMode.Tuna,
            TrafficProfile = TunaSoakTrafficProfile.FileOnly,
            Preset = TunaSoakPreset.TunaQuality,
            Payer = TunaSoakPayerMode.HelpeeOnly,
            Fault = TunaSoakFaultMode.None,
            Completed = true,
            SessionAlive = true,
            ChatControlAlive = true,
            FileCompleted = true,
            ScreenCompleted = true,
            FileBytesSent = 1024,
            FileBytesReceived = 1024,
            FileReceiveRatio = 1,
            TunaFrameCount = 1,
            DataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            FallbackStarted = true,
            FallbackFileSent = false,
            FallbackFileReceived = false,
            TerminalReason = string.Empty,
            SenderTerminalObserved = true,
            ReceiverTerminalObserved = true,
            FinalShaMatched = true,
            IsPhase6Gate = true,
        };
        result.V6EpochStarted = true;
        result.V6TargetProofObserved = true;
        result.V6RepairProofObserved = true;
        result.V6EpochRecovered = true;

        var summary = TunaSoakMatrixSummary.Build(new[] { result });

        Assert.Equal("pass", summary.Verdict);
        Assert.Empty(summary.Reasons);
        Assert.Contains(
            "phase6-clean-activation-unexpected-fallback:warning:unexpected_tuna_drop_recovered:file_receive_ratio=1.0000",
            summary.Warnings);
    }

    [Fact]
    public void TunaSidecarPhase6Summary_DoesNotReportShaMismatchWhenHashMatchedButCompletionTimedOut()
    {
        var result = new TunaSoakCellResult
        {
            CellId = "phase6-clean-activation-sender-timeout",
            Tier = TunaSoakTier.Core,
            Transport = Phase3TransportMode.Tuna,
            TrafficProfile = TunaSoakTrafficProfile.FileOnly,
            Preset = TunaSoakPreset.TunaQuality,
            Payer = TunaSoakPayerMode.BothUnlocked,
            Fault = TunaSoakFaultMode.None,
            Completed = false,
            SessionAlive = true,
            ChatControlAlive = true,
            FileCompleted = false,
            ScreenCompleted = true,
            FileBytesSent = 1024,
            FileBytesReceived = 1024,
            FileReceiveRatio = 1,
            FailureReason = "file_incomplete:receive_ratio=1.0000; reason=soak_timeout_incomplete",
            DataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            V6EpochStarted = true,
            V6TargetProofObserved = true,
            V6EpochRecovered = true,
            SenderTerminalObserved = true,
            ReceiverTerminalObserved = true,
            FinalShaMatched = true,
            IsPhase6Gate = true,
        };

        var summary = TunaSoakMatrixSummary.Build(new[] { result });

        Assert.Equal("fail", summary.Verdict);
        Assert.Contains("phase6-clean-activation-sender-timeout:file_incomplete", summary.Reasons);
        Assert.DoesNotContain("phase6-clean-activation-sender-timeout:final_sha_missing_or_mismatch", summary.Reasons);
    }

    [Fact]
    public async Task TunaSidecarPhase6ProviderQualityReport_WritesComparableRows()
    {
        var result = CreatePassingPhase6Result("phase6-provider-quality");
        result.ProviderDegradedAccepted = true;
        result.ProviderStillDegradedAtEnd = true;
        result.ProviderQualityClass = "persistent_missing_path";
        result.ProviderMissingIndices = [0];
        result.ProviderStable3OnlyMs = 12000;
        result.ProviderFinalUsableCount = 3;
        result.ProviderFinalPathReasons = ["0:empty_endpoint", "1:usable"];
        var summary = TunaSoakMatrixSummary.Build([result]);
        var artifactDir = Path.Combine(Path.GetTempPath(), "nlink-provider-quality-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactDir);

        await WriteProviderQualityReportAsync(artifactDir, summary, CancellationToken.None);

        var reportPath = Path.Combine(artifactDir, "provider-quality-report.json");
        Assert.True(File.Exists(reportPath));
        var text = await File.ReadAllTextAsync(reportPath);
        Assert.Contains("\"artifactKind\": \"tuna_provider_quality_report\"", text, StringComparison.Ordinal);
        Assert.Contains("\"providerQualityClass\": \"persistent_missing_path\"", text, StringComparison.Ordinal);
        Assert.Contains("\"providerMissingIndices\": [", text, StringComparison.Ordinal);
        Assert.Contains("\"providerStable3OnlyMs\": 12000", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TunaSidecarPhase6FaultDelay_FiresWhileFastFileTransferIsActive()
    {
        var snapshot = CaptureSoakEnvironment();
        try
        {
            ClearSoakEnvironment();

            var options = LoadPhase6ShortMatrixOptions();
            var cells = BuildPhase6ShortMatrixCells(options);
            var switchOff = Assert.Single(cells, static cell => cell.CellId == "phase6-tuna-file-helper-receiving-helpee-switch-off");
            var sidecarDrop = Assert.Single(cells, static cell => cell.CellId == "phase6-tuna-file-helper-receiving-both-sidecar-drop");

            Assert.Equal(TimeSpan.FromSeconds(30), ResolveTunaSoakFaultDelay(switchOff, options));
            Assert.Equal(TimeSpan.FromSeconds(30), ResolveTunaSoakFaultDelay(sidecarDrop, options));
        }
        finally
        {
            RestoreSoakEnvironment(snapshot);
        }
    }

    [Fact]
    public void TunaSidecarPhase6Redaction_RemovesSecretsAndFullPaths()
    {
        var repoRoot = FindRepoRoot();
        var walletPath = Path.Combine(repoRoot, "artifacts", "tuna-poc", "wallet-test-nkn.json");
        const string password = "phase6-secret";
        var raw = $"wallet={walletPath}; password={password}; seedBase64=abc; privateKey=def; session_id=session_raw; peer=peer_raw";

        var redacted = RedactPhase3ArtifactText(raw, walletPath, password);

        Assert.DoesNotContain(walletPath, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(password, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("seedBase64", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("privateKey", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wallet-test-nkn.json", redacted, StringComparison.Ordinal);
    }

    [Trait("Category", "Manual")]
    [ManualBridgeFact]
    public async Task TunaSidecarPhase6_ShortPaidMatrix_FileOnlyAcrossDirectionsPayersFaults()
    {
        if (!IsEnabled(Phase6ShortMatrixOptInEnv))
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var sidecarExe = Path.Combine(repoRoot, "artifacts", "tuna-sidecar", "nlink-tuna-sidecar.exe");
        var walletPath = Path.Combine(repoRoot, "artifacts", "tuna-poc", "wallet-test-nkn.json");
        var bridgeDir = TryFindBridgeBundleDirectory();
        var walletPassword = Environment.GetEnvironmentVariable(Phase3WalletPasswordEnv);
        var options = LoadPhase6ShortMatrixOptions();
        var cells = BuildPhase6ShortMatrixCells(options);
        var basePhase3Options = options.ToPhase3BenchmarkOptions(maxDurationOverrideSec: null);
        var phase3Options = basePhase3Options with
        {
            FileWriteBytes = Phase6FileWriteBytes,
            ListenerMaxTotalMiB = Math.Max(basePhase3Options.ListenerMaxTotalMiB, 4096),
        };
        var prerequisite = ValidatePhase3TunaPrerequisites(Phase3TransportMode.Tuna, sidecarExe, walletPath, walletPassword, phase3Options);

        Assert.True(File.Exists(sidecarExe), $"Missing Tuna sidecar: {sidecarExe}");
        Assert.True(File.Exists(walletPath), $"Missing Tuna test wallet: {Path.GetFileName(walletPath)}");
        Assert.True(bridgeDir is not null, "Bridge runtime not found. Build artifacts/bridge/win-x64 first.");
        Assert.True(prerequisite.IsValid, $"Phase 6 Tuna prerequisites failed: {prerequisite.Reason}");

        var artifactDir = CreatePhase6ArtifactDirectory(repoRoot, "phase6-short");
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
                        @event = "phase6_short_matrix_start",
                        options = options.ToArtifactModel(),
                        phase3Options = phase3Options.ToArtifactModel(),
                        cellCount = cells.Count,
                        walletFile = Path.GetFileName(walletPath),
                        bridgeRuntime = Path.GetFileName(bridgeDir!),
                        startedAtUtc = matrixStartedAtUtc,
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
                            cts.Token,
                            phase3Options,
                            useV6ServiceFileProfile: true);
                        MarkPhase6GateResult(result);
                        if (!ShouldRetryPhase6SoakCell(result, attempt, options.MaxCellRetries))
                        {
                            break;
                        }

                        await AppendPhase3EventAsync(
                            runsPath,
                            new
                            {
                                @event = "phase6_short_cell_retry",
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
                        @event = "phase6_short_matrix_aborted",
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
                await WritePhase6OperatorVerdictAsync(artifactDir, summary, terminalException, CancellationToken.None);
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

            Assert.True(terminalException is null, $"Phase 6 Tuna short matrix aborted after {results.Count}/{cells.Count} cells: {terminalException?.GetType().Name}:{terminalException?.Message}. Artifacts: {artifactDir}");
            Assert.Equal("pass", summary.Verdict);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ReleaseOverridePolicy.UnsafeDeveloperModeEnvVar, previousDeveloperMode);
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", previousNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", previousBridgePath);
            Environment.SetEnvironmentVariable("NLINK_RUN_MANUAL_BRIDGE", previousManualBridge);
        }
    }

    private static TunaSoakMatrixOptions LoadPhase6ShortMatrixOptions()
    {
        var baseOptions = TunaSoakMatrixOptions.Load();
        var duration = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SoakDurationMinutesEnv))
            ? TimeSpan.FromMinutes(Phase6DefaultDurationMinutes)
            : baseOptions.CellDuration;
        return baseOptions with
        {
            CellDuration = duration,
            FileSendPacingMbps = baseOptions.FileSendPacingMbps,
            MaxCellRetries = Math.Min(baseOptions.MaxCellRetries, 1),
            Tiers = [TunaSoakTier.Core],
        };
    }

    private static IReadOnlyList<TunaSoakMatrixCell> BuildPhase6ShortMatrixCells(TunaSoakMatrixOptions options)
    {
        var cells = new List<TunaSoakMatrixCell>();
        foreach (var receiverRole in new[] { TunaSoakReceiverRole.HelperReceiving, TunaSoakReceiverRole.HelpeeReceiving })
        {
            foreach (var payer in new[] { TunaSoakPayerMode.HelpeeOnly, TunaSoakPayerMode.HelperOnly, TunaSoakPayerMode.BothUnlocked })
            {
                cells.Add(CreatePhase6ShortCell(receiverRole, payer, TunaSoakFaultMode.None));
                cells.Add(CreatePhase6ShortCell(receiverRole, payer, GetPhase6FaultForPayer(payer)));
            }
        }

        if (options.CellFilters.Length == 0)
        {
            return cells;
        }

        var requested = options.CellFilters.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return cells.Where(cell => requested.Contains(cell.CellId)).ToArray();
    }

    private static TunaSoakMatrixCell CreatePhase6ShortCell(
        TunaSoakReceiverRole receiverRole,
        TunaSoakPayerMode payer,
        TunaSoakFaultMode fault)
        => new(
            TunaSoakTier.Core,
            $"phase6-tuna-file-{FormatPhase6ReceiverId(receiverRole)}-{FormatPhase6PayerId(payer)}-{FormatPhase6FaultId(fault)}",
            Phase3TransportMode.Tuna,
            TunaSoakTrafficProfile.FileOnly,
            TunaSoakPreset.TunaQuality,
            payer,
            fault)
        {
            ReceiverRole = receiverRole,
        };

    private static TunaSoakFaultMode GetPhase6FaultForPayer(TunaSoakPayerMode payer)
        => payer switch
        {
            TunaSoakPayerMode.HelpeeOnly => TunaSoakFaultMode.SwitchOffFallback,
            TunaSoakPayerMode.HelperOnly => TunaSoakFaultMode.CapReached,
            TunaSoakPayerMode.BothUnlocked => TunaSoakFaultMode.SidecarCrash,
            _ => TunaSoakFaultMode.SwitchOffFallback,
        };

    private static string FormatPhase6ReceiverId(TunaSoakReceiverRole receiverRole)
        => receiverRole == TunaSoakReceiverRole.HelperReceiving ? "helper-receiving" : "helpee-receiving";

    private static string FormatPhase6PayerId(TunaSoakPayerMode payer)
        => payer switch
        {
            TunaSoakPayerMode.HelperOnly => "helper",
            TunaSoakPayerMode.BothUnlocked => "both",
            _ => "helpee",
        };

    private static string FormatPhase6FaultId(TunaSoakFaultMode fault)
        => fault switch
        {
            TunaSoakFaultMode.None => "activation",
            TunaSoakFaultMode.SidecarCrash => "sidecar-drop",
            TunaSoakFaultMode.CapReached => "cap",
            _ => "switch-off",
        };

    private static string CreatePhase6ArtifactDirectory(string repoRoot, string prefix)
    {
        var artifactDir = ResolvePhase6ArtifactDirectory(
            repoRoot,
            Path.Combine(
                repoRoot,
                "artifacts",
                "tuna-sidecar",
                prefix + "-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)));
        Directory.CreateDirectory(artifactDir);
        return artifactDir;
    }

    private static string ResolvePhase6ArtifactDirectoryForTests(string repoRoot, string requestedPath)
        => ResolvePhase6ArtifactDirectory(repoRoot, requestedPath);

    private static string ResolvePhase6ArtifactDirectory(string repoRoot, string requestedPath)
    {
        var artifactsRoot = Path.GetFullPath(Path.Combine(repoRoot, "artifacts"));
        var candidate = Path.GetFullPath(requestedPath);
        var rootWithSeparator = artifactsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Phase 6 Tuna artifacts must be written under the repo artifacts directory.");
        }

        return candidate;
    }

    private static void MarkPhase6GateResult(TunaSoakCellResult result)
    {
        result.IsPhase6Gate = true;
        if (result.DataProtocolVersion == 0 && result.Completed)
        {
            result.DataProtocolVersion = FileTransferProtocol.ProtocolVersionV6;
        }
    }

    private static async Task WritePhase6OperatorVerdictAsync(
        string artifactDir,
        TunaSoakMatrixSummary summary,
        Exception? terminalException,
        CancellationToken ct)
    {
        var lines = new List<string>
        {
            "artifact_kind=tuna_phase6_short_matrix",
            "verdict=" + (terminalException is null && summary.Verdict == "pass" ? "PASS" : "FAIL"),
            "summary_verdict=" + summary.Verdict,
            "cell_count=" + summary.CellCount.ToString(CultureInfo.InvariantCulture),
            "passed_cells=" + summary.PassedCells.ToString(CultureInfo.InvariantCulture),
            "reason_count=" + summary.Reasons.Length.ToString(CultureInfo.InvariantCulture),
            "warning_count=" + summary.Warnings.Length.ToString(CultureInfo.InvariantCulture),
            "provider_degraded_accepted_cells=" + summary.ProviderDegradedAcceptedCells.ToString(CultureInfo.InvariantCulture),
            "provider_recovered_after_degraded_cells=" + summary.ProviderRecoveredAfterDegradedCells.ToString(CultureInfo.InvariantCulture),
            "provider_still_degraded_at_end_cells=" + summary.ProviderStillDegradedAtEndCells.ToString(CultureInfo.InvariantCulture),
            "first_read=phase6-operator-verdict.txt",
        };
        if (terminalException is not null)
        {
            lines.Add("terminal_exception=" + terminalException.GetType().Name + ":" + terminalException.Message);
        }

        lines.Add("reasons=" + (summary.Reasons.Length == 0 ? "(none)" : string.Join(",", summary.Reasons)));
        lines.Add("warnings=" + (summary.Warnings.Length == 0 ? "(none)" : string.Join(",", summary.Warnings)));
        await File.WriteAllLinesAsync(Path.Combine(artifactDir, "phase6-operator-verdict.txt"), lines, new UTF8Encoding(false), ct);
    }

    private static Task WriteProviderQualityReportAsync(
        string artifactDir,
        TunaSoakMatrixSummary summary,
        CancellationToken ct)
    {
        var report = new
        {
            artifactKind = "tuna_provider_quality_report",
            generatedAtUtc = DateTimeOffset.UtcNow,
            cellCount = summary.CellCount,
            rows = summary.Results.Select(static result => new
            {
                result.CellId,
                result.StartedUtc,
                result.EndedUtc,
                result.DurationMs,
                result.ProviderQualityClass,
                result.ProviderDegradedAccepted,
                result.ProviderRecoveredAfterDegraded,
                result.ProviderStillDegradedAtEnd,
                result.ProviderFirstDegradedUtc,
                result.ProviderRecoveredUtc,
                result.ProviderFinalUsableCount,
                result.ProviderMissingIndices,
                result.ProviderRecoveryLatencyMs,
                result.ProviderStable3OnlyMs,
                result.ProviderFinalPathReasons,
                fileStartedUtc = result.StartedUtc,
                fileEndedUtc = result.EndedUtc,
                fileThroughputMbps = result.FileThroughputMbps,
                fileReceiveRatio = result.FileReceiveRatio,
                fallbackStarted = result.FallbackStarted,
                fallbackExpected = result.FallbackExpected,
                v6EpochRecovered = result.V6EpochRecovered,
                v6EpochWaiting = result.V6EpochWaiting,
                sidecarOrphanCount = result.SidecarOrphanCount,
                warning = result.WarningReason,
                failure = result.FailureReason,
            }).ToArray(),
        };

        return File.WriteAllTextAsync(
            Path.Combine(artifactDir, "provider-quality-report.json"),
            JsonSerializer.Serialize(report, SoakJsonOptions),
            ct);
    }

    private static TunaSoakCellResult CreatePassingPhase6Result(string cellId)
        => new()
        {
            CellId = cellId,
            Tier = TunaSoakTier.Core,
            Transport = Phase3TransportMode.Tuna,
            TrafficProfile = TunaSoakTrafficProfile.FileOnly,
            Preset = TunaSoakPreset.TunaQuality,
            Payer = TunaSoakPayerMode.HelpeeOnly,
            Fault = TunaSoakFaultMode.None,
            Completed = true,
            SessionAlive = true,
            ChatControlAlive = true,
            FileCompleted = true,
            ScreenCompleted = true,
            FileBytesSent = 1024,
            FileBytesReceived = 1024,
            FileReceiveRatio = 1,
            TunaFrameCount = 1,
            DataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            V6EpochStarted = true,
            V6TargetProofObserved = true,
            V6EpochRecovered = true,
            SenderTerminalObserved = true,
            ReceiverTerminalObserved = true,
            FinalShaMatched = true,
            IsPhase6Gate = true,
        };
}
