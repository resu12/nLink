using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class TunaSidecarLiveManualTests
{
    private const string Phase3OptInEnv = "NLINK_RUN_PHASE3_TUNA_BENCHMARK";
    private const string Phase3WalletPasswordEnv = "NLINK_TUNA_TEST_WALLET_PASSWORD";
    private const string Phase3ScreenSmokeOnlyEnv = "NLINK_PHASE3_BENCHMARK_SCREEN_SMOKE_ONLY";
    private const string Phase3VerboseScreenDiagnosticsEnv = "NLINK_PHASE3_BENCHMARK_VERBOSE_SCREEN_DIAGNOSTICS";
    private const string Phase3MaxPriceNknPerMb = "0.0002";
    private static readonly TimeSpan Phase3DrainTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Phase3FallbackDrainTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Phase3FallbackTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Phase3ScreenSmokeFrameTimeout = TimeSpan.FromSeconds(20);
    private const int Phase3ScreenSmokeFrameCount = 3;
    private const int Phase3ScreenSmokeFrameAttempts = 3;
    private const double Phase3FileTailCloseMinimumReceiveRatio = 0.95;
    private const double Phase3FileSendPacingMbps = 45;

    [Fact]
    public void Phase3BenchmarkOptions_DefaultsRemainOptInAndThresholded()
    {
        var snapshot = CapturePhase3BenchmarkEnvironment();
        try
        {
            ClearPhase3BenchmarkEnvironment();
            var options = Phase3BenchmarkOptions.Load();

            Assert.Equal(3, options.RepeatCount);
            Assert.Equal(TimeSpan.FromSeconds(60), options.ProfileDuration);
            Assert.Equal(256L * 1024 * 1024, options.FileTargetBytes);
            Assert.Equal(FileTransferChunkBudget.MaxRawChunkBytes, options.FileWriteBytes);
            Assert.Equal(15, options.ScreenFps);
            Assert.Equal(384, options.ListenerMaxTotalMiB);
            Assert.Equal(180, options.ListenerMaxDurationSec);
            Assert.Equal(3, options.TunaSetupAttempts);
            Assert.Equal(1.25, options.FileThroughputPassRatio);
            Assert.False(options.ScreenSmokeOnly);
            Assert.False(options.VerboseScreenDiagnostics);
            Assert.Equal("NLINK_RUN_PHASE3_TUNA_BENCHMARK", Phase3OptInEnv);
            Assert.Equal("NLINK_PHASE3_BENCHMARK_SCREEN_SMOKE_ONLY", Phase3ScreenSmokeOnlyEnv);
            Assert.Equal("NLINK_PHASE3_BENCHMARK_VERBOSE_SCREEN_DIAGNOSTICS", Phase3VerboseScreenDiagnosticsEnv);
        }
        finally
        {
            RestorePhase3BenchmarkEnvironment(snapshot);
        }
    }

    [Fact]
    public void Phase3BenchmarkMode_BaselineDoesNotRequireTunaPrerequisites()
    {
        var options = Phase3BenchmarkOptions.Load();

        var baseline = ValidatePhase3TunaPrerequisites(
            Phase3TransportMode.Baseline,
            sidecarExe: null,
            walletPath: null,
            walletPassword: null,
            options);
        var tuna = ValidatePhase3TunaPrerequisites(
            Phase3TransportMode.Tuna,
            sidecarExe: null,
            walletPath: null,
            walletPassword: null,
            options);

        Assert.True(baseline.IsValid);
        Assert.False(baseline.RequiresTuna);
        Assert.False(tuna.IsValid);
        Assert.True(tuna.RequiresTuna);
        Assert.Equal("missing_sidecar_exe", tuna.Reason);
    }

    [Fact]
    public void Phase3BenchmarkArtifactRedaction_RemovesSecretsAndFullPaths()
    {
        const string password = "phase3-secret-password";
        var walletPath = Path.Combine("C:\\Users\\Juraj\\Desktop\\Remote help", "artifacts", "tuna-poc", "wallet-test-nkn.json");
        var input = $"wallet={walletPath}; password={password}; seedHex=abcdef; privateKey=123456";

        var redacted = RedactPhase3ArtifactText(input, walletPath, password);

        Assert.DoesNotContain(walletPath, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(password, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("123456", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("seedHex", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("privateKey", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wallet-test-nkn.json", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Phase3BenchmarkSummary_AttemptedFailedProfilesAreFailNotInconclusive()
    {
        var options = new Phase3BenchmarkOptions(
            RepeatCount: 1,
            ProfileDuration: TimeSpan.FromSeconds(3),
            FileTargetBytes: 1024 * 1024,
            FileWriteBytes: FileTransferChunkBudget.MaxRawChunkBytes,
            ScreenFps: 15,
            ScreenKeyFrameBytes: 96 * 1024,
            ScreenDeltaFrameBytes: 18 * 1024,
            ListenerMaxTotalMiB: 16,
            ListenerMaxDurationSec: 180,
            ListenerAcceptTimeoutSec: 180,
            TunaSetupAttempts: 1,
            FileSendPacingMbps: Phase3FileSendPacingMbps,
            FileFallbackPacingMbps: Phase3FileSendPacingMbps,
            FileThroughputPassRatio: 1.25,
            ScreenSmokeOnly: false,
            VerboseScreenDiagnostics: false);

        var runs = new[]
        {
            new Phase3RunResult { Profile = Phase3Profile.File, Mode = Phase3TransportMode.Baseline, Repeat = 1, Completed = true, ReceiverThroughputMbps = 4 },
            new Phase3RunResult { Profile = Phase3Profile.Screen, Mode = Phase3TransportMode.Baseline, Repeat = 1, LatencyP95Ms = 100, DropRate = 0, StallCount = 0 },
            Phase3RunResult.Failed("file-tuna-1", Phase3Profile.File, Phase3TransportMode.Tuna, 1, "TimeoutException:Condition was not met before timeout."),
            Phase3RunResult.Failed("screen-tuna-1", Phase3Profile.Screen, Phase3TransportMode.Tuna, 1, "TimeoutException:Condition was not met before timeout."),
            Phase3RunResult.Failed("reconnect-tuna-1", Phase3Profile.Reconnect, Phase3TransportMode.Tuna, 1, "TimeoutException:Condition was not met before timeout."),
        };

        var summary = Phase3BenchmarkSummary.Build(runs, options);

        Assert.Equal("fail", summary.Verdict);
        Assert.Contains("one_or_more_runs_failed", summary.Reasons);
    }

    [Fact]
    public void Phase3BenchmarkSummary_ScreenReadinessRequiresTunaMediaFlow()
    {
        var options = new Phase3BenchmarkOptions(
            RepeatCount: 1,
            ProfileDuration: TimeSpan.FromSeconds(3),
            FileTargetBytes: 1024 * 1024,
            FileWriteBytes: FileTransferChunkBudget.MaxRawChunkBytes,
            ScreenFps: 15,
            ScreenKeyFrameBytes: 96 * 1024,
            ScreenDeltaFrameBytes: 18 * 1024,
            ListenerMaxTotalMiB: 16,
            ListenerMaxDurationSec: 180,
            ListenerAcceptTimeoutSec: 180,
            TunaSetupAttempts: 1,
            FileSendPacingMbps: Phase3FileSendPacingMbps,
            FileFallbackPacingMbps: Phase3FileSendPacingMbps,
            FileThroughputPassRatio: 1.25,
            ScreenSmokeOnly: false,
            VerboseScreenDiagnostics: false);

        var runs = new[]
        {
            new Phase3RunResult { Profile = Phase3Profile.File, Mode = Phase3TransportMode.Baseline, Repeat = 1, Completed = true, ReceiverThroughputMbps = 4 },
            new Phase3RunResult { Profile = Phase3Profile.File, Mode = Phase3TransportMode.Tuna, Repeat = 1, Completed = true, ReceiverThroughputMbps = 6 },
            new Phase3RunResult { Profile = Phase3Profile.Screen, Mode = Phase3TransportMode.Baseline, Repeat = 1, SentFrames = 3, ReceivedFrames = 3, LatencyP95Ms = 100, DropRate = 0, StallCount = 0 },
            new Phase3RunResult
            {
                Profile = Phase3Profile.Screen,
                Mode = Phase3TransportMode.Tuna,
                Repeat = 1,
                SentFrames = 3,
                ReceivedFrames = 3,
                LatencyP95Ms = 100,
                DropRate = 0,
                StallCount = 0,
                ScreenFragmentsAttempted = 3,
                ScreenAcceleratedSendCount = 0,
                ScreenTunaWriteCount = 0,
                ScreenTunaReadCount = 0,
                ScreenFirstLossReason = "screen_route_to_tuna_missing",
            },
            new Phase3RunResult
            {
                Profile = Phase3Profile.Reconnect,
                Mode = Phase3TransportMode.Tuna,
                Repeat = 1,
                AccelerationAvailableAtStart = true,
                AccelerationUnavailableAfterKill = true,
                FallbackDelivered = true,
                SessionAliveAfterFallback = true,
            },
        };

        var summary = Phase3BenchmarkSummary.Build(runs, options);

        Assert.False(summary.ScreenPassed);
        Assert.Equal("fail", summary.Verdict);
        Assert.Contains("screen_tuna_readiness_not_met", summary.Reasons);
        Assert.Contains("screen_first_loss_screen_route_to_tuna_missing", summary.Reasons);
    }

    [Fact]
    public void Phase3BenchmarkSummary_FileReadinessRequiresTunaBulkFlow()
    {
        var options = new Phase3BenchmarkOptions(
            RepeatCount: 1,
            ProfileDuration: TimeSpan.FromSeconds(3),
            FileTargetBytes: 1024 * 1024,
            FileWriteBytes: FileTransferChunkBudget.MaxRawChunkBytes,
            ScreenFps: 15,
            ScreenKeyFrameBytes: 96 * 1024,
            ScreenDeltaFrameBytes: 18 * 1024,
            ListenerMaxTotalMiB: 16,
            ListenerMaxDurationSec: 180,
            ListenerAcceptTimeoutSec: 180,
            TunaSetupAttempts: 1,
            FileSendPacingMbps: Phase3FileSendPacingMbps,
            FileFallbackPacingMbps: Phase3FileSendPacingMbps,
            FileThroughputPassRatio: 1.25,
            ScreenSmokeOnly: false,
            VerboseScreenDiagnostics: false);

        var runs = new[]
        {
            new Phase3RunResult { Profile = Phase3Profile.File, Mode = Phase3TransportMode.Baseline, Repeat = 1, Completed = true, ReceiverThroughputMbps = 4 },
            new Phase3RunResult { Profile = Phase3Profile.File, Mode = Phase3TransportMode.Tuna, Repeat = 1, Completed = true, ReceiverThroughputMbps = 8 },
            new Phase3RunResult { Profile = Phase3Profile.Screen, Mode = Phase3TransportMode.Baseline, Repeat = 1, SentFrames = 3, ReceivedFrames = 3, LatencyP95Ms = 100, DropRate = 0, StallCount = 0 },
            new Phase3RunResult
            {
                Profile = Phase3Profile.Screen,
                Mode = Phase3TransportMode.Tuna,
                Repeat = 1,
                SentFrames = 3,
                ReceivedFrames = 3,
                LatencyP95Ms = 100,
                DropRate = 0,
                StallCount = 0,
                ScreenTunaWriteCount = 3,
                ScreenTunaReadCount = 3,
                ScreenFramesCompleted = 3,
            },
            new Phase3RunResult
            {
                Profile = Phase3Profile.Reconnect,
                Mode = Phase3TransportMode.Tuna,
                Repeat = 1,
                AccelerationAvailableAtStart = true,
                AccelerationUnavailableAfterKill = true,
                FallbackDelivered = true,
                SessionAliveAfterFallback = true,
            },
        };

        var summary = Phase3BenchmarkSummary.Build(runs, options);

        Assert.False(summary.FilePassed);
        Assert.Equal("fail", summary.Verdict);
        Assert.Contains("file_tuna_readiness_not_met", summary.Reasons);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task TunaSidecar_Phase3Benchmark_NknVsTuna_FileScreenAndReconnect()
    {
        if (!IsEnabled(Phase3OptInEnv))
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var sidecarExe = Path.Combine(repoRoot, "artifacts", "tuna-sidecar", "nlink-tuna-sidecar.exe");
        var walletPath = Path.Combine(repoRoot, "artifacts", "tuna-poc", "wallet-test-nkn.json");
        var bridgeDir = TryFindBridgeBundleDirectory();
        var walletPassword = Environment.GetEnvironmentVariable(Phase3WalletPasswordEnv);
        var options = Phase3BenchmarkOptions.Load();
        var prerequisite = ValidatePhase3TunaPrerequisites(Phase3TransportMode.Tuna, sidecarExe, walletPath, walletPassword, options);
        Assert.True(File.Exists(sidecarExe), $"Missing Tuna sidecar: {sidecarExe}");
        Assert.True(File.Exists(walletPath), $"Missing Tuna test wallet: {Path.GetFileName(walletPath)}");
        Assert.True(bridgeDir is not null, "Bridge runtime not found. Build artifacts/bridge/win-x64 first.");
        Assert.True(prerequisite.IsValid, $"Phase 3 Tuna prerequisites failed: {prerequisite.Reason}");

        var artifactDir = Path.Combine(
            repoRoot,
            "artifacts",
            "tuna-sidecar",
            "phase3-benchmark-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(artifactDir);
        var runsPath = Path.Combine(artifactDir, "runs.jsonl");
        var appLogStart = GetOperationalLogLength();

        var previousNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var previousBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var previousManualBridge = Environment.GetEnvironmentVariable("NLINK_RUN_MANUAL_BRIDGE");
        var runResults = new List<Phase3RunResult>();
        var listenerStdout = new ConcurrentQueue<string>();
        var listenerStderr = new ConcurrentQueue<string>();
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", Path.Combine(bridgeDir!, "node.exe"));
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", Path.Combine(bridgeDir!, "index.js"));
            Environment.SetEnvironmentVariable("NLINK_RUN_MANUAL_BRIDGE", "1");

            using var cts = new CancellationTokenSource(options.TotalLiveTimeout);
            await AppendPhase3EventAsync(
                runsPath,
                new
                {
                    @event = "benchmark_start",
                    options = options.ToArtifactModel(),
                    walletFile = Path.GetFileName(walletPath),
                    bridgeRuntime = Path.GetFileName(bridgeDir!),
                },
                cts.Token);

            var reconnectRecorded = false;
            for (var repeat = 1; repeat <= options.RepeatCount; repeat++)
            {
                foreach (var mode in new[] { Phase3TransportMode.Baseline, Phase3TransportMode.Tuna })
                {
                    if (mode == Phase3TransportMode.Tuna)
                    {
                        if (!options.ScreenSmokeOnly)
                        {
                            runResults.Add(await RunPhase3ProfileInFreshContextAsync(
                                Phase3Profile.File,
                                mode,
                                contextRepeat: repeat,
                                profileRepeat: repeat,
                                options,
                                sidecarExe,
                                walletPath,
                                walletPassword!,
                                runsPath,
                                listenerStdout,
                                listenerStderr,
                                cts.Token));
                        }

                        runResults.Add(await RunPhase3ProfileInFreshContextAsync(
                            Phase3Profile.Screen,
                            mode,
                            contextRepeat: repeat,
                            profileRepeat: repeat,
                            options,
                            sidecarExe,
                            walletPath,
                            walletPassword!,
                            runsPath,
                            listenerStdout,
                            listenerStderr,
                            cts.Token));
                        if (!options.ScreenSmokeOnly && repeat == options.RepeatCount)
                        {
                            runResults.Add(await RunPhase3ProfileInFreshContextAsync(
                                Phase3Profile.Reconnect,
                                mode,
                                contextRepeat: repeat,
                                profileRepeat: 1,
                                options,
                                sidecarExe,
                                walletPath,
                                walletPassword!,
                                runsPath,
                                listenerStdout,
                                listenerStderr,
                                cts.Token));
                            reconnectRecorded = true;
                        }

                        continue;
                    }

                    try
                    {
                        using var context = await CreatePhase3LiveRunContextWithRetryAsync(
                            mode,
                            repeat,
                            options,
                            sidecarExe,
                            walletPath,
                            walletPassword!,
                            runsPath,
                            listenerStdout,
                            listenerStderr,
                            cts.Token);
                        if (!options.ScreenSmokeOnly)
                        {
                            runResults.Add(await RunPhase3ProfileAsync(context, Phase3Profile.File, repeat, options, runsPath, cts.Token));
                        }

                        runResults.Add(await RunPhase3ProfileAsync(context, Phase3Profile.Screen, repeat, options, runsPath, cts.Token));
                        if (!options.ScreenSmokeOnly &&
                            mode == Phase3TransportMode.Tuna &&
                            repeat == options.RepeatCount)
                        {
                            runResults.Add(await RunPhase3ProfileAsync(context, Phase3Profile.Reconnect, repeat: 1, options, runsPath, cts.Token));
                            reconnectRecorded = true;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        if (!options.ScreenSmokeOnly)
                        {
                            runResults.Add(await AddFailedPhase3RunAsync(runsPath, Phase3Profile.File, mode, repeat, ex, cts.Token));
                        }

                        runResults.Add(await AddFailedPhase3RunAsync(runsPath, Phase3Profile.Screen, mode, repeat, ex, cts.Token));
                        if (!options.ScreenSmokeOnly &&
                            mode == Phase3TransportMode.Tuna &&
                            repeat == options.RepeatCount)
                        {
                            runResults.Add(await AddFailedPhase3RunAsync(runsPath, Phase3Profile.Reconnect, Phase3TransportMode.Tuna, repeat: 1, ex, cts.Token));
                            reconnectRecorded = true;
                        }
                    }
                }
            }

            if (!options.ScreenSmokeOnly && !reconnectRecorded)
            {
                runResults.Add(await AddFailedPhase3RunAsync(
                    runsPath,
                    Phase3Profile.Reconnect,
                    Phase3TransportMode.Tuna,
                    repeat: 1,
                    new InvalidOperationException("Tuna reconnect profile was not scheduled."),
                    cts.Token));
            }

            var summary = Phase3BenchmarkSummary.Build(runResults, options);
            await File.WriteAllTextAsync(
                Path.Combine(artifactDir, "summary.json"),
                JsonSerializer.Serialize(summary, Phase3JsonOptions),
                cts.Token);
            await File.WriteAllTextAsync(
                Path.Combine(artifactDir, "app-log-tail.redacted.log"),
                RedactPhase3ArtifactText(ReadOperationalLogTail(appLogStart), walletPath, walletPassword),
                cts.Token);
            if (!options.ScreenSmokeOnly)
            {
                Assert.NotEqual("inconclusive", summary.Verdict);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", previousNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", previousBridgePath);
            Environment.SetEnvironmentVariable("NLINK_RUN_MANUAL_BRIDGE", previousManualBridge);

            await File.WriteAllLinesAsync(
                Path.Combine(artifactDir, "listener.stdout.redacted.jsonl"),
                listenerStdout.Select(line => RedactPhase3ArtifactText(line, walletPath, walletPassword)));
            await File.WriteAllLinesAsync(
                Path.Combine(artifactDir, "listener.stderr.redacted.log"),
                listenerStderr.Select(line => RedactPhase3ArtifactText(line, walletPath, walletPassword)));
        }
    }

    private async Task<Phase3RunResult> RunPhase3ProfileInFreshContextAsync(
        Phase3Profile profile,
        Phase3TransportMode mode,
        int contextRepeat,
        int profileRepeat,
        Phase3BenchmarkOptions options,
        string sidecarExe,
        string walletPath,
        string walletPassword,
        string runsPath,
        ConcurrentQueue<string> listenerStdout,
        ConcurrentQueue<string> listenerStderr,
        CancellationToken ct)
    {
        try
        {
            using var context = await CreatePhase3LiveRunContextWithRetryAsync(
                mode,
                contextRepeat,
                options,
                sidecarExe,
                walletPath,
                walletPassword,
                runsPath,
                listenerStdout,
                listenerStderr,
                ct);
            return await RunPhase3ProfileAsync(context, profile, profileRepeat, options, runsPath, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await AddFailedPhase3RunAsync(runsPath, profile, mode, profileRepeat, ex, ct);
        }
    }

    private async Task<Phase3RunResult> RunPhase3ProfileAsync(
        Phase3LiveRunContext context,
        Phase3Profile profile,
        int repeat,
        Phase3BenchmarkOptions options,
        string runsPath,
        CancellationToken ct)
    {
        var runId = CreatePhase3RunId(profile, context.Mode, repeat);
        await AppendPhase3EventAsync(
            runsPath,
            new
            {
                @event = "run_start",
                runId,
                profile = profile.ToString().ToLowerInvariant(),
                mode = context.Mode.ToString().ToLowerInvariant(),
                repeat,
                startedAtUtc = DateTimeOffset.UtcNow,
            },
            ct);

        var logStart = GetOperationalLogLength();
        try
        {
            var result = profile switch
            {
                Phase3Profile.File => await RunPhase3FileProfileAsync(context, runId, repeat, options, logStart, ct),
                Phase3Profile.Screen => await RunPhase3ScreenProfileAsync(context, runId, repeat, options, logStart, runsPath, ct),
                Phase3Profile.Reconnect => await RunPhase3ReconnectProfileAsync(context, runId, repeat, options, logStart, ct),
                _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
            };
            await AppendPhase3EventAsync(runsPath, result, ct);
            return result;
        }
        catch (Exception ex)
        {
            var failed = Phase3RunResult.Failed(runId, profile, context.Mode, repeat, ex.GetType().Name + ":" + ex.Message);
            await AppendPhase3EventAsync(runsPath, failed, ct);
            return failed;
        }
    }

    private static async Task<Phase3RunResult> AddFailedPhase3RunAsync(
        string runsPath,
        Phase3Profile profile,
        Phase3TransportMode mode,
        int repeat,
        Exception ex,
        CancellationToken ct)
    {
        var failed = Phase3RunResult.Failed(
            CreatePhase3RunId(profile, mode, repeat),
            profile,
            mode,
            repeat,
            ex.GetType().Name + ":" + ex.Message);
        await AppendPhase3EventAsync(runsPath, failed, ct);
        return failed;
    }

    private async Task<Phase3LiveRunContext> CreatePhase3LiveRunContextWithRetryAsync(
        Phase3TransportMode mode,
        int repeat,
        Phase3BenchmarkOptions options,
        string sidecarExe,
        string walletPath,
        string walletPassword,
        string runsPath,
        ConcurrentQueue<string> listenerStdout,
        ConcurrentQueue<string> listenerStderr,
        CancellationToken ct)
    {
        var maxAttempts = mode == Phase3TransportMode.Tuna ? options.TunaSetupAttempts : 1;
        Exception? lastException = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var started = Stopwatch.StartNew();
            await AppendPhase3SetupEventAsync(
                runsPath,
                mode,
                repeat,
                attempt,
                maxAttempts,
                "setup",
                "started",
                durationMs: 0,
                reason: string.Empty,
                listenerLocalIpc: string.Empty,
                listenerAddressLength: 0,
                ct);
            try
            {
                var context = await CreatePhase3LiveRunContextAsync(
                    mode,
                    repeat,
                    attempt,
                    maxAttempts,
                    options,
                    sidecarExe,
                    walletPath,
                    walletPassword,
                    runsPath,
                    listenerStdout,
                    listenerStderr,
                    ct);
                await AppendPhase3SetupEventAsync(
                    runsPath,
                    mode,
                    repeat,
                    attempt,
                    maxAttempts,
                    "setup",
                    "succeeded",
                    Math.Max(1, (long)started.Elapsed.TotalMilliseconds),
                    reason: string.Empty,
                    listenerLocalIpc: context.ListenerReady?.LocalIpc ?? string.Empty,
                    listenerAddressLength: context.ListenerReady?.Address.Length ?? 0,
                    ct);
                return context;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastException = ex;
                var stage = TryGetPhase3SetupStage(ex);
                await AppendPhase3SetupEventAsync(
                    runsPath,
                    mode,
                    repeat,
                    attempt,
                    maxAttempts,
                    string.IsNullOrWhiteSpace(stage) ? "setup" : stage,
                    "failed",
                    Math.Max(1, (long)started.Elapsed.TotalMilliseconds),
                    reason: ex.GetType().Name + ":" + ex.Message,
                    listenerLocalIpc: string.Empty,
                    listenerAddressLength: 0,
                    ct);
                if (attempt >= maxAttempts)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Min(15, attempt * 3)), ct);
            }
        }

        throw new TimeoutException(
            $"Phase 3 {mode} setup failed after {maxAttempts} attempt(s): {lastException?.GetType().Name}:{lastException?.Message}",
            lastException);
    }

    private async Task<Phase3LiveRunContext> CreatePhase3LiveRunContextAsync(
        Phase3TransportMode mode,
        int repeat,
        int setupAttempt,
        int maxSetupAttempts,
        Phase3BenchmarkOptions options,
        string sidecarExe,
        string walletPath,
        string walletPassword,
        string? runsPath,
        ConcurrentQueue<string> listenerStdout,
        ConcurrentQueue<string> listenerStderr,
        CancellationToken ct)
    {
        var identityDir = Path.Combine(Path.GetTempPath(), "nlink-phase3-tuna-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(identityDir);
        Process? listenerProcess = null;
        RealNknClientAdapter? hostClient = null;
        RealNknClientAdapter? helperClient = null;
        NknSignalingTransport? host = null;
        NknSignalingTransport? helper = null;
        var setupStage = "identity";
        try
        {
            await AppendPhase3SetupEventAsync(runsPath, mode, repeat, setupAttempt, maxSetupAttempts, setupStage, "started", 0, string.Empty, string.Empty, 0, ct);
            var hostKey = Path.Combine(identityDir, "helpee-identity.json");
            var helperKey = Path.Combine(identityDir, "helper-identity.json");
            var hostOptionsBase = LoadNknOptionsWithOverrides(hostKey, "nlink-phase3-helpee-" + Guid.NewGuid().ToString("N")[..8]);
            var helperOptionsBase = LoadNknOptionsWithOverrides(helperKey, "nlink-phase3-helper-" + Guid.NewGuid().ToString("N")[..8]);
            var hostIdentity = NknIdentityStore.LoadOrCreate(hostOptionsBase);
            var helperIdentity = NknIdentityStore.LoadOrCreate(helperOptionsBase);
            hostClient = new RealNknClientAdapter(hostIdentity, hostOptionsBase);
            helperClient = new RealNknClientAdapter(helperIdentity, helperOptionsBase);
            await AppendPhase3SetupEventAsync(runsPath, mode, repeat, setupAttempt, maxSetupAttempts, setupStage, "succeeded", 0, string.Empty, string.Empty, 0, ct);

            NknTunaAccelerationOptions hostTunaOptions;
            NknTunaAccelerationOptions helperTunaOptions;
            INknAccelerationLane? hostLane = null;
            INknAccelerationLane? helperLane = null;
            ListenerReady? listenerReady = null;

            if (mode == Phase3TransportMode.Tuna)
            {
                setupStage = "resolve_dialer_address";
                await AppendPhase3SetupEventAsync(runsPath, mode, repeat, setupAttempt, maxSetupAttempts, setupStage, "started", 0, string.Empty, string.Empty, 0, ct);
                var helperSeedBase64 = NknIdentityStore.ReadSeedBase64ForConnect(helperOptionsBase.KeyPath);
                Assert.False(string.IsNullOrWhiteSpace(helperSeedBase64), "Helper identity seed is required for deterministic Tuna dialer identity.");
                var helperSidecarAddress = await ResolveSidecarAddressAsync(sidecarExe, helperSeedBase64!, ct);
                await AppendPhase3SetupEventAsync(runsPath, mode, repeat, setupAttempt, maxSetupAttempts, setupStage, "succeeded", 0, string.Empty, string.Empty, helperSidecarAddress.Length, ct);
                setupStage = "listener_ready";
                await AppendPhase3SetupEventAsync(runsPath, mode, repeat, setupAttempt, maxSetupAttempts, setupStage, "started", 0, string.Empty, string.Empty, 0, ct);
                listenerReady = await StartListenerSidecarAsync(
                    sidecarExe,
                    walletPath,
                    walletPassword,
                    helperSidecarAddress,
                listenerStdout,
                listenerStderr,
                ct,
                maxTotalMiB: options.ListenerMaxTotalMiB,
                maxDurationSec: options.ListenerMaxDurationSec,
                acceptTimeoutSec: options.ListenerAcceptTimeoutSec,
                maxPriceNknPerMb: Phase3MaxPriceNknPerMb,
                identifier: "nlink-phase3-listener-" + Guid.NewGuid().ToString("N")[..8]);
                listenerProcess = Process.GetProcessById(listenerReady.ProcessId);
                await AppendPhase3SetupEventAsync(runsPath, mode, repeat, setupAttempt, maxSetupAttempts, setupStage, "succeeded", 0, string.Empty, listenerReady.LocalIpc, listenerReady.Address.Length, ct);
                hostTunaOptions = CreateTunaOptionsForLiveTest(listenerReady.LocalIpc, sidecarExePath: null);
                helperTunaOptions = CreateTunaOptionsForLiveTest(listenerEndpoint: null, sidecarExe, helperSeedBase64);
                hostLane = new NknTunaAccelerationLane(hostTunaOptions);
                helperLane = new NknTunaAccelerationLane(helperTunaOptions);
            }
            else
            {
                hostTunaOptions = NknTunaAccelerationOptions.Disabled;
                helperTunaOptions = NknTunaAccelerationOptions.Disabled;
            }

            setupStage = "transport_construct";
            await AppendPhase3SetupEventAsync(runsPath, mode, repeat, setupAttempt, maxSetupAttempts, setupStage, "started", 0, string.Empty, string.Empty, 0, ct);
            host = new NknSignalingTransport(hostClient, hostOptionsBase, hostIdentity, hostTunaOptions, hostLane);
            helper = new NknSignalingTransport(helperClient, helperOptionsBase, helperIdentity, helperTunaOptions, helperLane);
            await AppendPhase3SetupEventAsync(runsPath, mode, repeat, setupAttempt, maxSetupAttempts, setupStage, "succeeded", 0, string.Empty, string.Empty, 0, ct);
            setupStage = "session_approval";
            await AppendPhase3SetupEventAsync(runsPath, mode, repeat, setupAttempt, maxSetupAttempts, setupStage, "started", 0, string.Empty, string.Empty, 0, ct);
            var sessionId = await ApproveLiveSessionAsync(
                host,
                helper,
                InviteCapabilities.Chat | InviteCapabilities.ScreenShare | InviteCapabilities.FileTransfer,
                ct);
            await AppendPhase3SetupEventAsync(runsPath, mode, repeat, setupAttempt, maxSetupAttempts, setupStage, "succeeded", 0, string.Empty, string.Empty, 0, ct);

            if (mode == Phase3TransportMode.Tuna)
            {
                setupStage = "acceleration_negotiation";
                await AppendPhase3SetupEventAsync(runsPath, mode, repeat, setupAttempt, maxSetupAttempts, setupStage, "started", 0, string.Empty, listenerReady?.LocalIpc ?? string.Empty, listenerReady?.Address.Length ?? 0, ct);
                await WaitUntilAsync(
                    () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                    TimeSpan.FromSeconds(120));
                await AppendPhase3SetupEventAsync(runsPath, mode, repeat, setupAttempt, maxSetupAttempts, setupStage, "succeeded", 0, string.Empty, listenerReady?.LocalIpc ?? string.Empty, listenerReady?.Address.Length ?? 0, ct);
            }

            var context = new Phase3LiveRunContext(
                mode,
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

    private async Task<Phase3RunResult> RunPhase3FileProfileAsync(
        Phase3LiveRunContext context,
        string runId,
        int repeat,
        Phase3BenchmarkOptions options,
        int logStart,
        CancellationToken ct)
    {
        var transferId = "phase3-file-" + Guid.NewGuid().ToString("N");
        await OpenPhase3FileTransferAsync(context, transferId, options.FileTargetBytes, options.FileWriteBytes, ct);
        using var receiverSession = await context.Host.OpenFileTransferDataSessionAsync(context.SessionId, transferId, ct);
        using var senderSession = await context.Helper.OpenFileTransferDataSessionAsync(context.SessionId, transferId, ct);
        var accelerationAvailableAtStart = IsPhase3TunaLaneReady(context, NknAccelerationLaneKind.File);
        if (context.Mode == Phase3TransportMode.Tuna && !accelerationAvailableAtStart)
        {
            return new Phase3RunResult
            {
                RunId = runId,
                Profile = Phase3Profile.File,
                Mode = context.Mode,
                Repeat = repeat,
                FailureReason = "file_tuna_lane_unavailable",
                AccelerationAvailableAtStart = false,
            };
        }

        var senderAccelerationStart = context.Helper.AccelerationDiagnosticsForTests;
        var receiverAccelerationStart = context.Host.AccelerationDiagnosticsForTests;
        using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var progress = new Phase3FileProgress(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var receiveTask = Task.Run(() => ReceivePhase3FileFramesAsync(receiverSession, progress, receiveCts.Token), CancellationToken.None);

        var sentBytes = 0L;
        var sentFrames = 0;
        var chunkIndex = 0;
        var started = Stopwatch.StartNew();
        try
        {
            while (started.Elapsed < options.ProfileDuration && sentBytes < options.FileTargetBytes)
            {
                var payloadBytes = (int)Math.Min(options.FileWriteBytes, options.FileTargetBytes - sentBytes);
                await senderSession.SendAsync(
                    CreatePhase3ChunkFrame(context.SessionId, transferId, chunkIndex, payloadBytes),
                    ct);
                sentBytes += payloadBytes;
                sentFrames++;
                chunkIndex++;
                await PacePhase3FileSendAsync(started, sentBytes, GetPhase3FilePacingMbps(context, options), ct);
                if (context.Mode == Phase3TransportMode.Tuna &&
                    !context.Host.IsAccelerationAvailableForTests &&
                    context.Helper.IsAccelerationAvailableForTests)
                {
                    await WaitUntilOrFalseAsync(
                        () => !context.Helper.IsAccelerationAvailableForTests,
                        TimeSpan.FromSeconds(2));
                }
            }

            var drainDeadline = DateTimeOffset.UtcNow + GetPhase3FileDrainTimeout(context);
            while (DateTimeOffset.UtcNow < drainDeadline && Volatile.Read(ref progress.BytesReceived) < sentBytes)
            {
                await Task.Delay(100, ct);
            }
        }
        finally
        {
            receiveCts.Cancel();
            try { await receiveTask.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None); } catch { }
        }

        var durationMs = Math.Max(1, (long)started.Elapsed.TotalMilliseconds);
        var receivedBytes = Volatile.Read(ref progress.BytesReceived);
        var receivedFrames = Volatile.Read(ref progress.FramesReceived);
        var completed = receivedBytes >= sentBytes && sentBytes > 0;
        await ClosePhase3FileTransferControlAsync(context, transferId, sentBytes, completed, ct);
        var accelerationDelta = CreatePhase3AccelerationLaneDelta(
            senderAccelerationStart,
            context.Helper.AccelerationDiagnosticsForTests,
            receiverAccelerationStart,
            context.Host.AccelerationDiagnosticsForTests,
            NknBridgeChannel.Bulk);
        var tunaBulkEvents = (int)Math.Min(accelerationDelta.FramesWritten, accelerationDelta.FramesReceived);
        var stalled = IsPhase3FileStalled(progress, sentBytes);
        var receivedRatio = sentBytes <= 0 ? 0 : receivedBytes / (double)sentBytes;
        var acceptableTailClose = receivedRatio >= Phase3FileTailCloseMinimumReceiveRatio && !stalled;
        var failureReason = string.Empty;
        if (context.Mode == Phase3TransportMode.Tuna)
        {
            if (!accelerationAvailableAtStart)
            {
                failureReason = "file_tuna_lane_unavailable";
            }
            else if (accelerationDelta.FramesWritten <= 0 || accelerationDelta.FramesReceived <= 0)
            {
                failureReason = "file_tuna_readiness_missing";
            }
            else if (!string.IsNullOrWhiteSpace(accelerationDelta.LastUnavailableReason) && !acceptableTailClose)
            {
                failureReason = "file_tuna_lane_unavailable:" + accelerationDelta.LastUnavailableReason;
            }
        }

        return new Phase3RunResult
        {
            RunId = runId,
            Profile = Phase3Profile.File,
            Mode = context.Mode,
            Repeat = repeat,
            DurationMs = durationMs,
            BytesSent = sentBytes,
            BytesReceived = receivedBytes,
            SentFrames = sentFrames,
            ReceivedFrames = receivedFrames,
            SenderThroughputMbps = ToMbps(sentBytes, durationMs),
            ReceiverThroughputMbps = ToMbps(receivedBytes, durationMs),
            Completed = completed,
            CapReached = sentBytes >= options.FileTargetBytes,
            StallCount = stalled ? 1 : 0,
            TunaFrameCount = tunaBulkEvents,
            NknFrameCount = Math.Max(0, sentFrames - (int)accelerationDelta.FramesAccepted),
            AccelerationAvailableAtStart = accelerationAvailableAtStart,
            AccelerationFramesAccepted = accelerationDelta.FramesAccepted,
            AccelerationFramesWritten = accelerationDelta.FramesWritten,
            AccelerationFramesReceived = accelerationDelta.FramesReceived,
            AccelerationSendRejected = accelerationDelta.SendRejected,
            AccelerationQueueOverflow = accelerationDelta.QueueOverflow,
            AccelerationLastUnavailableReason = accelerationDelta.LastUnavailableReason,
            FailureReason = failureReason,
        };
    }

    private static double GetPhase3FilePacingMbps(Phase3LiveRunContext context, Phase3BenchmarkOptions options)
        => context.Mode == Phase3TransportMode.Tuna &&
           (!context.Host.IsAccelerationAvailableForTests || !context.Helper.IsAccelerationAvailableForTests)
            ? Math.Min(options.FileSendPacingMbps, options.FileFallbackPacingMbps)
            : options.FileSendPacingMbps;

    private static TimeSpan GetPhase3FileDrainTimeout(Phase3LiveRunContext context)
        => context.Mode == Phase3TransportMode.Tuna &&
           (!context.Host.IsAccelerationAvailableForTests || !context.Helper.IsAccelerationAvailableForTests)
            ? Phase3FallbackDrainTimeout
            : Phase3DrainTimeout;

    private static async Task PacePhase3FileSendAsync(Stopwatch started, long sentBytes, double fileSendPacingMbps, CancellationToken ct)
    {
        var targetElapsedMs = sentBytes * 8d / (Math.Max(1, fileSendPacingMbps) * 1000d);
        var delayMs = targetElapsedMs - started.Elapsed.TotalMilliseconds;
        if (delayMs > 1)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(delayMs, 250)), ct);
        }
    }

    private async Task ClosePhase3FileTransferControlAsync(
        Phase3LiveRunContext context,
        string transferId,
        long fileSizeBytes,
        bool completed,
        CancellationToken ct)
    {
        var terminalReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnComplete(object? _, FileTransferCompleteReceivedEventArgs e)
        {
            if (string.Equals(e.Message.TransferId, transferId, StringComparison.Ordinal))
            {
                terminalReceived.TrySetResult();
            }
        }

        void OnCancel(object? _, FileTransferCancelReceivedEventArgs e)
        {
            if (string.Equals(e.Message.TransferId, transferId, StringComparison.Ordinal))
            {
                terminalReceived.TrySetResult();
            }
        }

        context.Helper.FileTransferCompleteReceived += OnComplete;
        context.Helper.FileTransferCancelReceived += OnCancel;
        try
        {
            if (completed)
            {
                await context.Host.SendFileTransferCompleteAsync(
                    new FileTransferCompleteV1
                    {
                        SessionId = context.SessionId,
                        TransferId = transferId,
                        FileSizeBytes = fileSizeBytes,
                        Sha256Base64 = Convert.ToBase64String(new byte[32]),
                    },
                    ct);
            }
            else
            {
                await context.Host.SendFileTransferCancelAsync(
                    new FileTransferCancelV1
                    {
                        SessionId = context.SessionId,
                        TransferId = transferId,
                        Reason = "phase3_profile_cleanup",
                    },
                    ct);
            }

            await terminalReceived.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        finally
        {
            context.Helper.FileTransferCompleteReceived -= OnComplete;
            context.Helper.FileTransferCancelReceived -= OnCancel;
        }
    }

    private async Task<Phase3RunResult> RunPhase3ScreenProfileAsync(
        Phase3LiveRunContext context,
        string runId,
        int repeat,
        Phase3BenchmarkOptions options,
        int logStart,
        string runsPath,
        CancellationToken ct)
    {
        var streamEpoch = repeat * 100 + (context.Mode == Phase3TransportMode.Tuna ? 2 : 1);
        var observer = new Phase3ScreenRunObserver(context.SessionId, streamEpoch);
        var diagnostics = new Phase3ScreenDiagnostics();
        var accelerationAvailableAtStart = IsPhase3TunaLaneReady(context, NknAccelerationLaneKind.Screen);
        var configReceived = new TaskCompletionSource<ScreenShareVideoStreamConfigReceivedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnConfig(object? _, ScreenShareVideoStreamConfigReceivedEventArgs e)
        {
            if (string.Equals(e.Message.SessionId, context.SessionId, StringComparison.Ordinal))
            {
                configReceived.TrySetResult(e);
            }
        }

        context.Host.ScreenShareVideoStreamConfigReceived += OnConfig;
        context.Host.ScreenShareFrameCompleted += observer.OnFrame;
        try
        {
            await context.Helper.SendScreenShareVideoStreamConfigAsync(
                CreateScreenShareVideoStreamConfig(context.SessionId, streamEpoch),
                ct);
            await configReceived.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

            if (context.Mode == Phase3TransportMode.Tuna && !accelerationAvailableAtStart)
            {
                return BuildPhase3ScreenRunResult(
                    context,
                    runId,
                    repeat,
                    durationMs: 1,
                    sentFrames: 0,
                    sentTransportFrames: 0,
                    accelerationAvailableAtStart,
                    observer,
                    diagnostics,
                    CreatePhase3AccelerationLaneDelta(
                        context.Helper.AccelerationDiagnosticsForTests,
                        context.Helper.AccelerationDiagnosticsForTests,
                        context.Host.AccelerationDiagnosticsForTests,
                        context.Host.AccelerationDiagnosticsForTests,
                        NknBridgeChannel.Media),
                    logStart,
                    screenSmokePassed: false,
                    screenSmokeOnly: options.ScreenSmokeOnly,
                    failureReason: "screen_smoke_failed:tuna_screen_lane_unavailable");
            }

            var nextFrameId = 0L;
            var smokeSenderAccelerationStart = context.Helper.AccelerationDiagnosticsForTests;
            var smokeReceiverAccelerationStart = context.Host.AccelerationDiagnosticsForTests;
            var smokeResult = await RunPhase3ScreenSmokeAsync(
                context,
                runId,
                streamEpoch,
                nextFrameId,
                options,
                diagnostics,
                observer,
                runsPath,
                ct);
            var smokeAccelerationDelta = CreatePhase3AccelerationLaneDelta(
                smokeSenderAccelerationStart,
                context.Helper.AccelerationDiagnosticsForTests,
                smokeReceiverAccelerationStart,
                context.Host.AccelerationDiagnosticsForTests,
                NknBridgeChannel.Media);
            var smokeExpectedCompletedFrames = (int)Math.Max(0, smokeResult.NextFrameId - nextFrameId);
            nextFrameId = smokeResult.NextFrameId;
            var tunaSmokeReadinessPassed = context.Mode != Phase3TransportMode.Tuna ||
                                           smokeAccelerationDelta.FramesWritten > 0 &&
                                           smokeAccelerationDelta.FramesReceived > 0 &&
                                           observer.ReceivedFrames >= smokeExpectedCompletedFrames;
            if (!smokeResult.Passed || !tunaSmokeReadinessPassed)
            {
                return BuildPhase3ScreenRunResult(
                    context,
                    runId,
                    repeat,
                    durationMs: smokeResult.DurationMs,
                    sentFrames: smokeResult.FramesSent,
                    sentTransportFrames: smokeResult.TransportFramesSent,
                    accelerationAvailableAtStart,
                    observer,
                    diagnostics,
                    smokeAccelerationDelta,
                    logStart,
                    screenSmokePassed: false,
                    screenSmokeOnly: options.ScreenSmokeOnly,
                    failureReason: "screen_smoke_failed:" + (smokeResult.Passed ? "tuna_smoke_readiness_missing" : smokeResult.Reason));
            }

            if (options.ScreenSmokeOnly)
            {
                var smokeOnlyResult = BuildPhase3ScreenRunResult(
                    context,
                    runId,
                    repeat,
                    durationMs: smokeResult.DurationMs,
                    sentFrames: smokeResult.FramesSent,
                    sentTransportFrames: smokeResult.TransportFramesSent,
                    accelerationAvailableAtStart,
                    observer,
                    diagnostics,
                    smokeAccelerationDelta,
                    logStart,
                    screenSmokePassed: true,
                    screenSmokeOnly: true,
                    failureReason: string.Empty);
                if (options.VerboseScreenDiagnostics)
                {
                    await AppendPhase3EventAsync(
                        runsPath,
                        new
                        {
                            @event = "screen_completion_observations",
                            runId,
                            streamEpoch,
                            frames = observer.GetCompletionObservations(),
                        },
                        ct);
                }

                return smokeOnlyResult;
            }

            observer.ResetStats();
            diagnostics.Reset();
            var fullProfileLogStart = GetOperationalLogLength();
            var fullSenderAccelerationStart = context.Helper.AccelerationDiagnosticsForTests;
            var fullReceiverAccelerationStart = context.Host.AccelerationDiagnosticsForTests;
            var framePeriodMs = 1000d / options.ScreenFps;
            var keyframeInterval = Math.Max(1, options.ScreenFps * 2);
            var started = Stopwatch.StartNew();
            var frameId = nextFrameId;
            var sentTransportFrames = 0;
            while (started.Elapsed < options.ProfileDuration)
            {
                var isKeyFrame = frameId % keyframeInterval == 0;
                sentTransportFrames += await SendPhase3ScreenFrameAsync(
                    context.Helper,
                    runId,
                    context.SessionId,
                    streamEpoch,
                    frameId,
                    isKeyFrame,
                    isKeyFrame ? options.ScreenKeyFrameBytes : options.ScreenDeltaFrameBytes,
                    isSmokeFrame: false,
                    diagnostics,
                    runsPath,
                    options.VerboseScreenDiagnostics,
                    capturedTsUtcMsOverride: null,
                    ct);
                frameId++;

                var targetMs = (frameId - nextFrameId) * framePeriodMs;
                var delayMs = targetMs - started.Elapsed.TotalMilliseconds;
                if (delayMs > 1)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct);
                }
            }

            await Task.Delay(Phase3DrainTimeout, ct);
            var durationMs = Math.Max(1, (long)started.Elapsed.TotalMilliseconds);
            if (options.VerboseScreenDiagnostics)
            {
                await AppendPhase3EventAsync(
                    runsPath,
                    new
                    {
                        @event = "screen_completion_observations",
                        runId,
                        streamEpoch,
                        frames = observer.GetCompletionObservations(),
                    },
                    ct);
            }

            return BuildPhase3ScreenRunResult(
                context,
                runId,
                repeat,
                durationMs,
                sentFrames: (int)(frameId - nextFrameId),
                sentTransportFrames,
                accelerationAvailableAtStart,
                observer,
                diagnostics,
                CreatePhase3AccelerationLaneDelta(
                    fullSenderAccelerationStart,
                    context.Helper.AccelerationDiagnosticsForTests,
                    fullReceiverAccelerationStart,
                    context.Host.AccelerationDiagnosticsForTests,
                    NknBridgeChannel.Media),
                fullProfileLogStart,
                screenSmokePassed: true,
                screenSmokeOnly: false,
                failureReason: string.Empty);
        }
        finally
        {
            context.Host.ScreenShareVideoStreamConfigReceived -= OnConfig;
            context.Host.ScreenShareFrameCompleted -= observer.OnFrame;
        }
    }

    private async Task<Phase3ScreenSmokeResult> RunPhase3ScreenSmokeAsync(
        Phase3LiveRunContext context,
        string runId,
        int streamEpoch,
        long startFrameId,
        Phase3BenchmarkOptions options,
        Phase3ScreenDiagnostics diagnostics,
        Phase3ScreenRunObserver observer,
        string runsPath,
        CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var framesSent = 0;
        var sentTransportFrames = 0;
        for (var i = 0; i < Phase3ScreenSmokeFrameCount; i++)
        {
            var frameId = startFrameId + i;
            var capturedTsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var frameCompleted = false;
            for (var attempt = 1; attempt <= Phase3ScreenSmokeFrameAttempts; attempt++)
            {
                sentTransportFrames += await SendPhase3ScreenFrameAsync(
                    context.Helper,
                    runId,
                    context.SessionId,
                    streamEpoch,
                    frameId,
                    isKeyFrame: i == 0,
                    i == 0 ? options.ScreenKeyFrameBytes : options.ScreenDeltaFrameBytes,
                    isSmokeFrame: true,
                    diagnostics,
                    runsPath,
                    options.VerboseScreenDiagnostics,
                    capturedTsUtcMs,
                    ct);
                framesSent++;
                if (await observer.WaitForFrameAsync(frameId, Phase3ScreenSmokeFrameTimeout, ct))
                {
                    frameCompleted = true;
                    break;
                }
            }

            if (!frameCompleted)
            {
                return new Phase3ScreenSmokeResult(
                    Passed: false,
                    Reason: $"frame_{frameId}_timeout",
                    FramesSent: framesSent,
                    TransportFramesSent: sentTransportFrames,
                    NextFrameId: frameId + 1,
                    DurationMs: Math.Max(1, (long)started.Elapsed.TotalMilliseconds));
            }
        }

        return new Phase3ScreenSmokeResult(
            Passed: true,
            Reason: string.Empty,
            FramesSent: framesSent,
            TransportFramesSent: sentTransportFrames,
            NextFrameId: startFrameId + 3,
            DurationMs: Math.Max(1, (long)started.Elapsed.TotalMilliseconds));
    }

    private static Phase3RunResult BuildPhase3ScreenRunResult(
        Phase3LiveRunContext context,
        string runId,
        int repeat,
        long durationMs,
        int sentFrames,
        int sentTransportFrames,
        bool accelerationAvailableAtStart,
        Phase3ScreenRunObserver observer,
        Phase3ScreenDiagnostics diagnostics,
        Phase3AccelerationLaneDelta accelerationDelta,
        int logStart,
        bool screenSmokePassed,
        bool screenSmokeOnly,
        string failureReason)
    {
        var logCounters = ReadPhase3ScreenLogCounters(logStart);
        var received = observer.ReceivedFrames;
        var latencySnapshot = observer.GetLatencySnapshot();
        var fallbackCount = context.Mode == Phase3TransportMode.Tuna
            ? Math.Max(0, sentTransportFrames - (int)accelerationDelta.FramesAccepted)
            : sentTransportFrames;
        var firstLossReason = InferPhase3ScreenFirstLossReason(
            context.Mode,
            accelerationAvailableAtStart,
            sentTransportFrames,
            received,
            accelerationDelta,
            logCounters);

        return new Phase3RunResult
        {
            RunId = runId,
            Profile = Phase3Profile.Screen,
            Mode = context.Mode,
            Repeat = repeat,
            DurationMs = Math.Max(1, durationMs),
            SentFrames = sentFrames,
            ReceivedFrames = received,
            DropRate = sentFrames == 0 ? 0 : Math.Max(0, sentFrames - received) / (double)sentFrames,
            StallCount = observer.StallCount,
            LatencyP50Ms = Percentile(latencySnapshot, 50),
            LatencyP95Ms = Percentile(latencySnapshot, 95),
            LatencyP99Ms = Percentile(latencySnapshot, 99),
            TunaFrameCount = context.Mode == Phase3TransportMode.Tuna
                ? (int)Math.Min(accelerationDelta.FramesWritten, accelerationDelta.FramesReceived)
                : 0,
            NknFrameCount = fallbackCount,
            Completed = string.IsNullOrWhiteSpace(failureReason),
            AccelerationAvailableAtStart = accelerationAvailableAtStart,
            ScreenFragmentsAttempted = diagnostics.FragmentsAttempted,
            ScreenFragmentsSendCompleted = diagnostics.FragmentsSendCompleted,
            ScreenSendFailureCount = diagnostics.SendFailureCount,
            ScreenAcceleratedSendCount = (int)accelerationDelta.FramesAccepted,
            ScreenTunaWriteCount = (int)accelerationDelta.FramesWritten,
            ScreenTunaReadCount = (int)accelerationDelta.FramesReceived,
            ScreenNknFallbackCount = fallbackCount,
            ScreenAcceleratedRejectCount = logCounters.AcceleratedFrameRejects,
            ScreenSecureRejectCount = logCounters.ScreenShareRejects,
            ScreenFramesCompleted = received,
            ScreenSmokePassed = screenSmokePassed,
            ScreenSmokeOnly = screenSmokeOnly,
            ScreenFirstLossReason = firstLossReason,
            AccelerationFramesAccepted = accelerationDelta.FramesAccepted,
            AccelerationFramesWritten = accelerationDelta.FramesWritten,
            AccelerationFramesReceived = accelerationDelta.FramesReceived,
            AccelerationSendRejected = accelerationDelta.SendRejected,
            AccelerationQueueOverflow = accelerationDelta.QueueOverflow,
            AccelerationLastUnavailableReason = accelerationDelta.LastUnavailableReason,
            FailureReason = failureReason,
        };
    }

    private static Phase3ScreenLogCounters ReadPhase3ScreenLogCounters(int logStart)
    {
        var logTail = ReadOperationalLogTail(logStart);
        return new Phase3ScreenLogCounters(
            AcceleratedMediaSends: CountOccurrences(logTail, "event=tuna_accelerated_envelope_sent; message_type=ScreenShareFrame; channel=media"),
            SidecarMediaWrites: CountOccurrences(logTail, "event=tuna_sidecar_frame_written; channel=media"),
            SidecarMediaReads: CountOccurrences(logTail, "event=tuna_sidecar_frame_received; channel=media"),
            AcceleratedFrameRejects: CountOccurrences(logTail, "event=tuna_accelerated_frame_rejected"),
            AccelerationMessageRejects: CountOccurrences(logTail, "event=tuna_acceleration_message_rejected"),
            ScreenShareRejects: CountOccurrences(logTail, "event=screen_share_message_rejected"));
    }

    private static Phase3AccelerationLaneDelta CreatePhase3AccelerationLaneDelta(
        NknAccelerationLaneDiagnostics senderBefore,
        NknAccelerationLaneDiagnostics senderAfter,
        NknAccelerationLaneDiagnostics receiverBefore,
        NknAccelerationLaneDiagnostics receiverAfter,
        NknBridgeChannel channel)
        => new(
            FramesAccepted: Math.Max(0, senderAfter.AcceptedFor(channel) - senderBefore.AcceptedFor(channel)),
            FramesWritten: Math.Max(0, senderAfter.WrittenFor(channel) - senderBefore.WrittenFor(channel)),
            FramesReceived: Math.Max(0, receiverAfter.ReceivedFor(channel) - receiverBefore.ReceivedFor(channel)),
            SendRejected: Math.Max(0, senderAfter.SendRejected - senderBefore.SendRejected),
            QueueOverflow: Math.Max(0, senderAfter.QueueOverflow - senderBefore.QueueOverflow),
            LastUnavailableReason: string.IsNullOrWhiteSpace(senderAfter.LastUnavailableReason)
                ? receiverAfter.LastUnavailableReason
                : senderAfter.LastUnavailableReason);

    private static bool IsPhase3TunaLaneReady(Phase3LiveRunContext context, NknAccelerationLaneKind lane)
        => context.Mode != Phase3TransportMode.Tuna ||
           context.Host.IsAccelerationAvailableForTests &&
           context.Helper.IsAccelerationAvailableForTests &&
           (context.Host.AccelerationNegotiatedLanesForTests & lane) == lane &&
           (context.Helper.AccelerationNegotiatedLanesForTests & lane) == lane;

    private static string InferPhase3ScreenFirstLossReason(
        Phase3TransportMode mode,
        bool accelerationAvailableAtStart,
        int sentTransportFrames,
        int receivedFrames,
        Phase3AccelerationLaneDelta accelerationDelta,
        Phase3ScreenLogCounters logCounters)
    {
        if (sentTransportFrames <= 0)
        {
            return "screen_no_frames_sent";
        }

        if (mode == Phase3TransportMode.Tuna)
        {
            if (!accelerationAvailableAtStart)
            {
                return "acceleration_unavailable_at_start";
            }

            if (accelerationDelta.FramesAccepted == 0 && accelerationDelta.FramesWritten == 0)
            {
                return "screen_route_to_tuna_missing";
            }

            if (accelerationDelta.FramesWritten > 0 && accelerationDelta.FramesReceived == 0)
            {
                return "sidecar_delivery_missing";
            }

            if (accelerationDelta.FramesReceived > 0 && logCounters.AcceleratedFrameRejects > 0)
            {
                return "accelerated_envelope_rejected";
            }

            if (accelerationDelta.FramesReceived > 0 && logCounters.ScreenShareRejects > 0)
            {
                return "screen_secure_envelope_rejected";
            }

            if (accelerationDelta.FramesReceived > 0 && receivedFrames == 0)
            {
                return "screen_reconstruction_missing";
            }
        }

        if (logCounters.ScreenShareRejects > 0)
        {
            return "screen_secure_envelope_rejected";
        }

        return receivedFrames == 0 ? "screen_completion_missing" : string.Empty;
    }

    private async Task<Phase3RunResult> RunPhase3ReconnectProfileAsync(
        Phase3LiveRunContext context,
        string runId,
        int repeat,
        Phase3BenchmarkOptions options,
        int logStart,
        CancellationToken ct)
    {
        Assert.Equal(Phase3TransportMode.Tuna, context.Mode);
        var transferId = "phase3-reconnect-" + Guid.NewGuid().ToString("N");
        var stage = "open_file_transfer_control";
        var started = Stopwatch.StartNew();
        var accelerationAvailableAtStart = false;
        try
        {
            await OpenPhase3FileTransferAsync(context, transferId, 2 * options.FileWriteBytes, options.FileWriteBytes, ct);
            stage = "open_file_data_sessions";
            using var receiverSession = await context.Host.OpenFileTransferDataSessionAsync(context.SessionId, transferId, ct);
            using var senderSession = await context.Helper.OpenFileTransferDataSessionAsync(context.SessionId, transferId, ct);
            stage = "check_acceleration_ready";
            accelerationAvailableAtStart =
                IsPhase3TunaLaneReady(context, NknAccelerationLaneKind.File) &&
                IsPhase3TunaLaneReady(context, NknAccelerationLaneKind.Screen);
            if (!accelerationAvailableAtStart)
            {
                return new Phase3RunResult
                {
                    RunId = runId,
                    Profile = Phase3Profile.Reconnect,
                    Mode = context.Mode,
                    Repeat = repeat,
                    DurationMs = Math.Max(1, (long)started.Elapsed.TotalMilliseconds),
                    FailureReason = "reconnect_tuna_lane_unavailable",
                    AccelerationAvailableAtStart = false,
                };
            }

            var senderAccelerationStart = context.Helper.AccelerationDiagnosticsForTests;
            var receiverAccelerationStart = context.Host.AccelerationDiagnosticsForTests;

            var configReceived = new TaskCompletionSource<ScreenShareVideoStreamConfigReceivedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var screenBeforeKill = new TaskCompletionSource<ScreenShareFrameCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var screenAfterKill = new TaskCompletionSource<ScreenShareFrameCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnConfig(object? _, ScreenShareVideoStreamConfigReceivedEventArgs e)
            {
                if (string.Equals(e.Message.SessionId, context.SessionId, StringComparison.Ordinal) && e.Message.StreamEpoch == 1)
                {
                    configReceived.TrySetResult(e);
                }
            }

            void OnFrame(object? _, ScreenShareFrameCompletedEventArgs e)
            {
                if (!string.Equals(e.SessionId, context.SessionId, StringComparison.Ordinal))
                {
                    return;
                }

                if (e.FrameId == 0)
                {
                    screenBeforeKill.TrySetResult(e);
                }
                else if (e.FrameId == 1)
                {
                    screenAfterKill.TrySetResult(e);
                }
            }

            context.Host.ScreenShareVideoStreamConfigReceived += OnConfig;
            context.Host.ScreenShareFrameCompleted += OnFrame;
            try
            {
                stage = "send_screen_config_before_kill";
                await context.Helper.SendScreenShareVideoStreamConfigAsync(CreateScreenShareVideoStreamConfig(context.SessionId, 1), ct);
                await WaitPhase3StageAsync(configReceived.Task, TimeSpan.FromSeconds(30), "screen_config_before_kill", ct);
                stage = "send_screen_frame_before_kill";
                if (!await SendPhase3ScreenFrameUntilCompletedAsync(
                        context.Helper,
                        screenBeforeKill.Task,
                        context.SessionId,
                        streamEpoch: 1,
                        frameId: 0,
                        isKeyFrame: true,
                        options.ScreenDeltaFrameBytes,
                        TimeSpan.FromSeconds(30),
                        ct))
                {
                    throw new TimeoutException("screen_frame_before_kill");
                }

                stage = "send_file_frame_before_kill";
                await senderSession.SendAsync(CreatePhase3ChunkFrame(context.SessionId, transferId, 0, options.FileWriteBytes), ct);
                var beforeKillFile = await WaitPhase3StageAsync(receiverSession.ReceiveAsync(ct).AsTask(), TimeSpan.FromSeconds(30), "file_frame_before_kill", ct);
                Assert.Equal(0, ((FileTransferChunkBatchFrame)beforeKillFile).StartChunkIndex);

                stage = "kill_listener";
                context.KillListener();
                var accelerationDown = await WaitUntilOrFalseAsync(
                    () => !context.Host.IsAccelerationAvailableForTests || !context.Helper.IsAccelerationAvailableForTests,
                    Phase3FallbackTimeout);

                stage = "send_file_frame_after_kill";
                var fileFallbackDelivered = false;
                var fileFallbackDeadline = DateTimeOffset.UtcNow + Phase3FallbackTimeout;
                while (!fileFallbackDelivered && DateTimeOffset.UtcNow < fileFallbackDeadline)
                {
                    await senderSession.SendAsync(CreatePhase3ChunkFrame(context.SessionId, transferId, 1, options.FileWriteBytes), ct);
                    var remaining = fileFallbackDeadline - DateTimeOffset.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                    {
                        break;
                    }

                    var afterKillFile = await TryReceivePhase3FileFrameAsync(
                        receiverSession,
                        remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1),
                        ct);
                    fileFallbackDelivered = afterKillFile is FileTransferChunkBatchFrame afterKillBatch &&
                                            afterKillBatch.StartChunkIndex == 1;
                }

                if (!fileFallbackDelivered)
                {
                    throw new TimeoutException("file_frame_after_kill");
                }

                stage = "send_screen_frame_after_kill";
                if (!await SendPhase3ScreenFrameUntilCompletedAsync(
                        context.Helper,
                        screenAfterKill.Task,
                        context.SessionId,
                        streamEpoch: 1,
                        frameId: 1,
                        isKeyFrame: false,
                        options.ScreenDeltaFrameBytes,
                        Phase3FallbackTimeout,
                        ct))
                {
                    throw new TimeoutException("screen_frame_after_kill");
                }

                var screenFallbackDelivered = true;
                var sessionAlive = IsPhase3SessionAlive(context);
                var bulkAccelerationDelta = CreatePhase3AccelerationLaneDelta(
                    senderAccelerationStart,
                    context.Helper.AccelerationDiagnosticsForTests,
                    receiverAccelerationStart,
                    context.Host.AccelerationDiagnosticsForTests,
                    NknBridgeChannel.Bulk);
                var mediaAccelerationDelta = CreatePhase3AccelerationLaneDelta(
                    senderAccelerationStart,
                    context.Helper.AccelerationDiagnosticsForTests,
                    receiverAccelerationStart,
                    context.Host.AccelerationDiagnosticsForTests,
                    NknBridgeChannel.Media);
                var tunaFrameCount = (int)(
                    Math.Min(bulkAccelerationDelta.FramesWritten, bulkAccelerationDelta.FramesReceived) +
                    Math.Min(mediaAccelerationDelta.FramesWritten, mediaAccelerationDelta.FramesReceived));

                return new Phase3RunResult
                {
                    RunId = runId,
                    Profile = Phase3Profile.Reconnect,
                    Mode = context.Mode,
                    Repeat = repeat,
                    DurationMs = Math.Max(1, (long)started.Elapsed.TotalMilliseconds),
                    SentFrames = 4,
                    ReceivedFrames = 4,
                    Completed = true,
                    AccelerationAvailableAtStart = accelerationAvailableAtStart,
                    AccelerationUnavailableAfterKill = accelerationDown,
                    FallbackDelivered = fileFallbackDelivered && screenFallbackDelivered,
                    SessionAliveAfterFallback = sessionAlive,
                    TunaFrameCount = tunaFrameCount,
                    NknFrameCount = 2,
                    AccelerationFramesAccepted = bulkAccelerationDelta.FramesAccepted + mediaAccelerationDelta.FramesAccepted,
                    AccelerationFramesWritten = bulkAccelerationDelta.FramesWritten + mediaAccelerationDelta.FramesWritten,
                    AccelerationFramesReceived = bulkAccelerationDelta.FramesReceived + mediaAccelerationDelta.FramesReceived,
                    AccelerationSendRejected = bulkAccelerationDelta.SendRejected + mediaAccelerationDelta.SendRejected,
                    AccelerationQueueOverflow = bulkAccelerationDelta.QueueOverflow + mediaAccelerationDelta.QueueOverflow,
                    AccelerationLastUnavailableReason = string.IsNullOrWhiteSpace(mediaAccelerationDelta.LastUnavailableReason)
                        ? bulkAccelerationDelta.LastUnavailableReason
                        : mediaAccelerationDelta.LastUnavailableReason,
                    FailureReason = accelerationAvailableAtStart && accelerationDown && fileFallbackDelivered && screenFallbackDelivered && sessionAlive
                        ? string.Empty
                        : "fallback_gate_failed",
                };
            }
            finally
            {
                context.Host.ScreenShareVideoStreamConfigReceived -= OnConfig;
                context.Host.ScreenShareFrameCompleted -= OnFrame;
            }
        }
        catch (TimeoutException ex)
        {
            return new Phase3RunResult
            {
                RunId = runId,
                Profile = Phase3Profile.Reconnect,
                Mode = context.Mode,
                Repeat = repeat,
                DurationMs = Math.Max(1, (long)started.Elapsed.TotalMilliseconds),
                AccelerationAvailableAtStart = accelerationAvailableAtStart,
                FailureReason = "reconnect_timeout:" + (string.IsNullOrWhiteSpace(ex.Message) ? stage : ex.Message),
            };
        }
    }

    private static async Task<bool> SendPhase3ScreenFrameUntilCompletedAsync(
        NknSignalingTransport sender,
        Task completion,
        string sessionId,
        int streamEpoch,
        long frameId,
        bool isKeyFrame,
        int payloadBytes,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!completion.IsCompleted && DateTimeOffset.UtcNow < deadline)
        {
            await SendPhase3ScreenFrameAsync(sender, sessionId, streamEpoch, frameId, isKeyFrame, payloadBytes, ct);
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var delay = remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1);
            await Task.WhenAny(completion, Task.Delay(delay, ct));
        }

        return completion.IsCompletedSuccessfully;
    }

    private static async Task<FileTransferDataFrame?> TryReceivePhase3FileFrameAsync(
        IFileTransferDataSession receiverSession,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        receiveCts.CancelAfter(timeout);
        try
        {
            return await receiverSession.ReceiveAsync(receiveCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task OpenPhase3FileTransferAsync(
        Phase3LiveRunContext context,
        string transferId,
        long fileSizeBytes,
        int chunkSizeBytes,
        CancellationToken ct)
    {
        var offerReceived = new TaskCompletionSource<FileTransferOfferV2>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acceptReceived = new TaskCompletionSource<FileTransferAcceptV1>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionOpenReceived = new TaskCompletionSource<FileTransferSessionOpenV2>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnOffer(object? _, FileTransferOfferReceivedEventArgs e)
        {
            if (string.Equals(e.Message.TransferId, transferId, StringComparison.Ordinal))
            {
                offerReceived.TrySetResult(e.Message);
            }
        }

        void OnAccept(object? _, FileTransferAcceptReceivedEventArgs e)
        {
            if (string.Equals(e.Message.TransferId, transferId, StringComparison.Ordinal))
            {
                acceptReceived.TrySetResult(e.Message);
            }
        }

        void OnOpen(object? _, FileTransferSessionOpenReceivedEventArgs e)
        {
            if (string.Equals(e.Message.TransferId, transferId, StringComparison.Ordinal))
            {
                sessionOpenReceived.TrySetResult(e.Message);
            }
        }

        context.Host.FileTransferOfferReceived += OnOffer;
        context.Helper.FileTransferAcceptReceived += OnAccept;
        context.Host.FileTransferSessionOpenReceived += OnOpen;
        try
        {
            await context.Helper.SendFileTransferOfferAsync(
                new FileTransferOfferV2
                {
                    SessionId = context.SessionId,
                    TransferId = transferId,
                    FileName = "phase3-benchmark.bin",
                    FileSizeBytes = fileSizeBytes,
                    PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV5,
                },
                ct);
            await WaitPhase3StageAsync(offerReceived.Task, TimeSpan.FromSeconds(30), "file_transfer_offer", ct);
            await context.Host.SendFileTransferAcceptAsync(
                new FileTransferAcceptV1
                {
                    SessionId = context.SessionId,
                    TransferId = transferId,
                    AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV5,
                },
                ct);
            await WaitPhase3StageAsync(acceptReceived.Task, TimeSpan.FromSeconds(30), "file_transfer_accept", ct);
            await context.Helper.SendFileTransferSessionOpenAsync(
                new FileTransferSessionOpenV2
                {
                    SessionId = context.SessionId,
                    TransferId = transferId,
                    ProtocolVersion = FileTransferProtocol.ProtocolVersionV5,
                    SessionRole = FileTransferProtocol.SessionRoleSender,
                    ChunkSizeBytes = chunkSizeBytes,
                    InitialPipelineDepth = 8,
                },
                ct);
            await WaitPhase3StageAsync(sessionOpenReceived.Task, TimeSpan.FromSeconds(30), "file_transfer_session_open", ct);
        }
        finally
        {
            context.Host.FileTransferOfferReceived -= OnOffer;
            context.Helper.FileTransferAcceptReceived -= OnAccept;
            context.Host.FileTransferSessionOpenReceived -= OnOpen;
        }
    }

    private static async Task ReceivePhase3FileFramesAsync(
        IFileTransferDataSession receiverSession,
        Phase3FileProgress progress,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var frame = await receiverSession.ReceiveAsync(ct);
                var bytes = GetPhase3FrameBytes(frame);
                Interlocked.Add(ref progress.BytesReceived, bytes);
                Interlocked.Increment(ref progress.FramesReceived);
                Interlocked.Exchange(ref progress.LastReceiveUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static FileTransferChunkBatchFrameV5 CreatePhase3ChunkFrame(
        string sessionId,
        string transferId,
        int chunkIndex,
        int payloadBytes)
        => new()
        {
            SessionId = sessionId,
            TransferId = transferId,
            StartChunkIndex = chunkIndex,
            ChunkCount = 1,
            DataSegments = new[] { CreatePhase3Payload(payloadBytes, chunkIndex) },
            BatchProfile = "phase3_benchmark_64k",
        };

    private static async Task<int> SendPhase3ScreenFrameAsync(
        NknSignalingTransport sender,
        string sessionId,
        int streamEpoch,
        long frameId,
        bool isKeyFrame,
        int payloadBytes,
        CancellationToken ct)
        => await SendPhase3ScreenFrameAsync(
            sender,
            runId: null,
            sessionId,
            streamEpoch,
            frameId,
            isKeyFrame,
            payloadBytes,
            isSmokeFrame: false,
            diagnostics: null,
            runsPath: null,
            verboseDiagnostics: false,
            capturedTsUtcMsOverride: null,
            ct);

    private static async Task<int> SendPhase3ScreenFrameAsync(
        NknSignalingTransport sender,
        string? runId,
        string sessionId,
        int streamEpoch,
        long frameId,
        bool isKeyFrame,
        int payloadBytes,
        bool isSmokeFrame,
        Phase3ScreenDiagnostics? diagnostics,
        string? runsPath,
        bool verboseDiagnostics,
        long? capturedTsUtcMsOverride,
        CancellationToken ct)
    {
        var fragmentCount = Math.Max(1, (int)Math.Ceiling(payloadBytes / (double)ScreenShareVideoPayloadCodec.MaxFragmentRawBytes));
        var capturedTsUtcMs = capturedTsUtcMsOverride ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sendStartedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sentFragments = 0;
        diagnostics?.ObserveFragmentsAttempted(fragmentCount);
        try
        {
            for (var fragmentIndex = 0; fragmentIndex < fragmentCount; fragmentIndex++)
            {
                var remaining = payloadBytes - fragmentIndex * ScreenShareVideoPayloadCodec.MaxFragmentRawBytes;
                var fragmentBytes = Math.Min(ScreenShareVideoPayloadCodec.MaxFragmentRawBytes, remaining);
                await sender.SendScreenSharePayloadAsync(
                    CreatePhase3ScreenFragmentPayload(
                        sessionId,
                        streamEpoch,
                        frameId,
                        capturedTsUtcMs,
                        isKeyFrame,
                        fragmentIndex,
                        fragmentCount,
                        fragmentBytes),
                    ct);
                sentFragments++;
                diagnostics?.ObserveFragmentSendCompleted();
            }

            return fragmentCount;
        }
        catch
        {
            diagnostics?.ObserveSendFailure();
            throw;
        }
        finally
        {
            if (verboseDiagnostics && !string.IsNullOrWhiteSpace(runsPath) && !string.IsNullOrWhiteSpace(runId))
            {
                await AppendPhase3EventAsync(
                    runsPath,
                    new
                    {
                        @event = "screen_frame_sent",
                        runId,
                        streamEpoch,
                        frameId,
                        isKeyFrame,
                        isSmokeFrame,
                        payloadBytes,
                        fragmentCount,
                        sentFragments,
                        capturedTsUtcMs,
                        sendStartedUtcMs,
                        sendCompletedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    },
                    ct);
            }
        }
    }

    private static ReadOnlyMemory<byte> CreatePhase3ScreenFragmentPayload(
        string sessionId,
        int streamEpoch,
        long frameId,
        long capturedTsUtcMs,
        bool isKeyFrame,
        int fragmentIndex,
        int fragmentCount,
        int fragmentBytes)
        => ScreenShareVideoPayloadCodec.SerializeFragment(
            new ScreenShareVideoFragmentV1
            {
                SessionId = sessionId,
                StreamEpoch = streamEpoch,
                FrameId = frameId,
                Width = 1280,
                Height = 720,
                CapturedTsUtcMs = capturedTsUtcMs,
                Encoding = "h264",
                IsKeyFrame = isKeyFrame,
                FragmentIndex = fragmentIndex,
                FragmentCount = fragmentCount,
                Data = CreatePhase3Payload(fragmentBytes, frameId + fragmentIndex),
            });

    private static byte[] CreatePhase3Payload(int byteCount, long salt)
    {
        var payload = new byte[byteCount];
        if (payload.Length == 0)
        {
            return payload;
        }

        payload[0] = (byte)(salt & 0xff);
        payload[^1] = (byte)((salt >> 8) & 0xff);
        return payload;
    }

    private static long GetPhase3FrameBytes(FileTransferDataFrame frame)
        => frame is FileTransferChunkBatchFrameV5 batch
            ? batch.DataSegments.Sum(static segment => segment?.Length ?? 0)
            : 0;

    private static bool IsPhase3FileStalled(Phase3FileProgress progress, long sentBytes)
    {
        if (sentBytes <= 0)
        {
            return false;
        }

        var received = Volatile.Read(ref progress.BytesReceived);
        if (received <= 0)
        {
            return true;
        }

        var ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Volatile.Read(ref progress.LastReceiveUnixMs);
        return ageMs > 10_000 && received < sentBytes;
    }

    private static bool IsPhase3SessionAlive(Phase3LiveRunContext context)
    {
        var now = DateTimeOffset.UtcNow;
        return string.Equals(context.Host.CurrentSessionSecurityState.SessionId?.Value, context.SessionId, StringComparison.Ordinal) &&
               string.Equals(context.Helper.CurrentSessionSecurityState.SessionId?.Value, context.SessionId, StringComparison.Ordinal) &&
               context.Host.CurrentSessionSecurityState.HandshakeState == SessionHandshakeState.Verified &&
               context.Helper.CurrentSessionSecurityState.HandshakeState == SessionHandshakeState.Verified &&
               context.Host.CurrentSessionSecurityState.IsApprovalActive(now) &&
               context.Helper.CurrentSessionSecurityState.IsApprovalActive(now);
    }

    private static async Task WaitPhase3StageAsync(Task task, TimeSpan timeout, string stage, CancellationToken ct)
    {
        try
        {
            await task.WaitAsync(timeout, ct);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(stage, ex);
        }
    }

    private static async Task<T> WaitPhase3StageAsync<T>(Task<T> task, TimeSpan timeout, string stage, CancellationToken ct)
    {
        try
        {
            return await task.WaitAsync(timeout, ct);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(stage, ex);
        }
    }

    private static Phase3PrerequisiteResult ValidatePhase3TunaPrerequisites(
        Phase3TransportMode mode,
        string? sidecarExe,
        string? walletPath,
        string? walletPassword,
        Phase3BenchmarkOptions options)
    {
        if (mode == Phase3TransportMode.Baseline)
        {
            return new Phase3PrerequisiteResult(true, false, string.Empty);
        }

        if (string.IsNullOrWhiteSpace(sidecarExe))
        {
            return new Phase3PrerequisiteResult(false, true, "missing_sidecar_exe");
        }

        if (string.IsNullOrWhiteSpace(walletPath))
        {
            return new Phase3PrerequisiteResult(false, true, "missing_wallet_path");
        }

        if (string.IsNullOrWhiteSpace(walletPassword))
        {
            return new Phase3PrerequisiteResult(false, true, "missing_wallet_password");
        }

        if (options.ListenerMaxTotalMiB <= 0 ||
            options.ListenerMaxDurationSec <= 0 ||
            string.IsNullOrWhiteSpace(Phase3MaxPriceNknPerMb))
        {
            return new Phase3PrerequisiteResult(false, true, "missing_spending_caps");
        }

        return new Phase3PrerequisiteResult(true, true, string.Empty);
    }

    private static string RedactPhase3ArtifactText(string? text, string? walletPath, string? walletPassword)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var redacted = text;
        if (!string.IsNullOrWhiteSpace(walletPath))
        {
            redacted = redacted.Replace(walletPath, Path.GetFileName(walletPath), StringComparison.OrdinalIgnoreCase);
            redacted = redacted.Replace(walletPath.Replace('\\', '/'), Path.GetFileName(walletPath), StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrEmpty(walletPassword))
        {
            redacted = redacted.Replace(walletPassword, "<redacted>", StringComparison.Ordinal);
        }

        redacted = Regex.Replace(
            redacted,
            @"(?i)\b(password|seedHex|seedBase64|seed|privateKey|private_key)\b\s*[:=]\s*[""']?[^;,\s""'}]+[""']?",
            "<redacted-secret>");
        redacted = Regex.Replace(
            redacted,
            @"[A-Za-z]:\\(?:[^\\/:*?""<>|\r\n]+\\)+([^\\/:*?""<>|\r\n]+)",
            "$1");
        return redacted;
    }

    private static Dictionary<string, string?> CapturePhase3BenchmarkEnvironment()
        => Phase3BenchmarkEnvironmentNames.ToDictionary(static name => name, Environment.GetEnvironmentVariable);

    private static void ClearPhase3BenchmarkEnvironment()
    {
        foreach (var name in Phase3BenchmarkEnvironmentNames)
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    private static void RestorePhase3BenchmarkEnvironment(IReadOnlyDictionary<string, string?> values)
    {
        foreach (var (name, value) in values)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static Task AppendPhase3EventAsync(string path, object value, CancellationToken ct)
        => File.AppendAllTextAsync(path, JsonSerializer.Serialize(value, Phase3JsonOptions) + Environment.NewLine, ct);

    private static Task AppendPhase3SetupEventAsync(
        string? path,
        Phase3TransportMode mode,
        int repeat,
        int attempt,
        int maxAttempts,
        string stage,
        string status,
        long durationMs,
        string reason,
        string listenerLocalIpc,
        int listenerAddressLength,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.CompletedTask;
        }

        return AppendPhase3EventAsync(
            path,
            new
            {
                @event = "phase3_setup",
                mode = mode.ToString().ToLowerInvariant(),
                repeat,
                attempt,
                maxAttempts,
                stage,
                status,
                durationMs,
                reason,
                listenerLocalIpc,
                listenerAddressLength,
                timestampUtc = DateTimeOffset.UtcNow,
            },
            ct);
    }

    private static string TryGetPhase3SetupStage(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current.Data["phase3_setup_stage"] is string stage && !string.IsNullOrWhiteSpace(stage))
            {
                return stage;
            }
        }

        return string.Empty;
    }

    private static string CreatePhase3RunId(Phase3Profile profile, Phase3TransportMode mode, int repeat)
    {
        var value = $"{profile.ToString().ToLowerInvariant()}-{mode.ToString().ToLowerInvariant()}-{repeat}-{Guid.NewGuid():N}";
        return value.Length <= 64 ? value : value[..64];
    }

    private static double ToMbps(long bytes, long durationMs)
        => durationMs <= 0 ? 0 : bytes * 8d / durationMs / 1000d;

    private static double Median(IEnumerable<double> values)
        => Percentile(values, 50);

    private static double Percentile(IEnumerable<double> values, int percentile)
    {
        var ordered = values.Where(static value => !double.IsNaN(value) && value >= 0).OrderBy(static value => value).ToArray();
        if (ordered.Length == 0)
        {
            return -1;
        }

        var index = (int)Math.Ceiling(percentile / 100d * ordered.Length) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
    }

    private static readonly JsonSerializerOptions Phase3JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly string[] Phase3BenchmarkEnvironmentNames =
    [
        "NLINK_PHASE3_BENCHMARK_REPEATS",
        "NLINK_PHASE3_BENCHMARK_DURATION_SEC",
        "NLINK_PHASE3_BENCHMARK_FILE_MIB",
        "NLINK_PHASE3_BENCHMARK_SCREEN_FPS",
        "NLINK_PHASE3_BENCHMARK_LISTENER_MAX_MIB",
        "NLINK_PHASE3_BENCHMARK_LISTENER_MAX_DURATION_SEC",
        "NLINK_PHASE3_BENCHMARK_LISTENER_ACCEPT_TIMEOUT_SEC",
        "NLINK_PHASE3_BENCHMARK_TUNA_SETUP_ATTEMPTS",
        Phase3ScreenSmokeOnlyEnv,
        Phase3VerboseScreenDiagnosticsEnv,
    ];

    private sealed class Phase3LiveRunContext : IDisposable
    {
        public Phase3LiveRunContext(
            Phase3TransportMode mode,
            string identityDir,
            string sessionId,
            NknSignalingTransport host,
            NknSignalingTransport helper,
            RealNknClientAdapter hostClient,
            RealNknClientAdapter helperClient,
            Process? listenerProcess,
            ListenerReady? listenerReady)
        {
            Mode = mode;
            IdentityDir = identityDir;
            SessionId = sessionId;
            Host = host;
            Helper = helper;
            HostClient = hostClient;
            HelperClient = helperClient;
            ListenerProcess = listenerProcess;
            ListenerReady = listenerReady;
        }

        public Phase3TransportMode Mode { get; }

        public string IdentityDir { get; }

        public string SessionId { get; }

        public NknSignalingTransport Host { get; }

        public NknSignalingTransport Helper { get; }

        public RealNknClientAdapter HostClient { get; }

        public RealNknClientAdapter HelperClient { get; }

        public Process? ListenerProcess { get; private set; }

        public ListenerReady? ListenerReady { get; }

        public void KillListener()
        {
            if (ListenerProcess is null)
            {
                return;
            }

            TryKill(ListenerProcess);
            ListenerProcess = null;
        }

        public void Dispose()
        {
            Host.Dispose();
            Helper.Dispose();
            HostClient.Dispose();
            HelperClient.Dispose();
            KillListener();
            try { Directory.Delete(IdentityDir, recursive: true); } catch { }
        }
    }

    private sealed class Phase3FileProgress
    {
        public Phase3FileProgress(long lastReceiveUnixMs)
            => LastReceiveUnixMs = lastReceiveUnixMs;

        public long BytesReceived;

        public int FramesReceived;

        public long LastReceiveUnixMs;
    }

    private sealed class Phase3ScreenDiagnostics
    {
        private long fragmentsAttempted;
        private long fragmentsSendCompleted;
        private long sendFailureCount;

        public int FragmentsAttempted => (int)Volatile.Read(ref fragmentsAttempted);

        public int FragmentsSendCompleted => (int)Volatile.Read(ref fragmentsSendCompleted);

        public int SendFailureCount => (int)Volatile.Read(ref sendFailureCount);

        public void ObserveFragmentsAttempted(int count)
            => Interlocked.Add(ref fragmentsAttempted, count);

        public void ObserveFragmentSendCompleted()
            => Interlocked.Increment(ref fragmentsSendCompleted);

        public void ObserveSendFailure()
            => Interlocked.Increment(ref sendFailureCount);

        public void Reset()
        {
            Interlocked.Exchange(ref fragmentsAttempted, 0);
            Interlocked.Exchange(ref fragmentsSendCompleted, 0);
            Interlocked.Exchange(ref sendFailureCount, 0);
        }
    }

    private sealed class Phase3ScreenRunObserver
    {
        private readonly string sessionId;
        private readonly long streamEpoch;
        private readonly ConcurrentQueue<double> latencies = new();
        private readonly ConcurrentQueue<Phase3ScreenFrameCompletionObservation> completionObservations = new();
        private readonly ConcurrentDictionary<long, long> completedFrames = new();
        private readonly ConcurrentDictionary<long, TaskCompletionSource> completionWaiters = new();
        private long lastArrivalMs;
        private int receivedFrames;
        private int stallCount;

        public Phase3ScreenRunObserver(string sessionId, long streamEpoch)
        {
            this.sessionId = sessionId;
            this.streamEpoch = streamEpoch;
        }

        public int ReceivedFrames => Volatile.Read(ref receivedFrames);

        public int StallCount => Volatile.Read(ref stallCount);

        public void OnFrame(object? _, ScreenShareFrameCompletedEventArgs e)
        {
            if (!string.Equals(e.SessionId, sessionId, StringComparison.Ordinal) || e.StreamEpoch != streamEpoch)
            {
                return;
            }

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var latencyMs = e.CapturedTsUtcMs > 0 ? Math.Max(0, nowMs - e.CapturedTsUtcMs) : -1;
            if (latencyMs >= 0)
            {
                latencies.Enqueue(latencyMs);
            }

            var previous = Interlocked.Exchange(ref lastArrivalMs, nowMs);
            if (previous > 0 && nowMs - previous > 500)
            {
                Interlocked.Increment(ref stallCount);
            }

            completionObservations.Enqueue(new Phase3ScreenFrameCompletionObservation(
                e.FrameId,
                e.StreamEpoch,
                e.IsKeyFrame,
                e.CapturedTsUtcMs,
                nowMs,
                latencyMs,
                e.EncodedFrameBytes.Length));
            completedFrames[e.FrameId] = nowMs;
            if (completionWaiters.TryGetValue(e.FrameId, out var waiter))
            {
                waiter.TrySetResult();
            }

            Interlocked.Increment(ref receivedFrames);
        }

        public async Task<bool> WaitForFrameAsync(long frameId, TimeSpan timeout, CancellationToken ct)
        {
            if (completedFrames.ContainsKey(frameId))
            {
                return true;
            }

            var waiter = completionWaiters.GetOrAdd(
                frameId,
                static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            if (completedFrames.ContainsKey(frameId))
            {
                waiter.TrySetResult();
            }

            try
            {
                await waiter.Task.WaitAsync(timeout, ct);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        public double[] GetLatencySnapshot()
            => latencies.ToArray();

        public Phase3ScreenFrameCompletionObservation[] GetCompletionObservations()
            => completionObservations.ToArray();

        public void ResetStats()
        {
            while (latencies.TryDequeue(out _))
            {
            }

            while (completionObservations.TryDequeue(out _))
            {
            }

            completedFrames.Clear();
            completionWaiters.Clear();
            Interlocked.Exchange(ref lastArrivalMs, 0);
            Interlocked.Exchange(ref receivedFrames, 0);
            Interlocked.Exchange(ref stallCount, 0);
        }
    }

    private sealed record Phase3ScreenFrameCompletionObservation(
        long FrameId,
        long StreamEpoch,
        bool IsKeyFrame,
        long CapturedTsUtcMs,
        long CompletedUtcMs,
        double LatencyMs,
        int EncodedBytes);

    private sealed record Phase3ScreenSmokeResult(
        bool Passed,
        string Reason,
        int FramesSent,
        int TransportFramesSent,
        long NextFrameId,
        long DurationMs);

    private sealed record Phase3ScreenLogCounters(
        int AcceleratedMediaSends,
        int SidecarMediaWrites,
        int SidecarMediaReads,
        int AcceleratedFrameRejects,
        int AccelerationMessageRejects,
        int ScreenShareRejects);

    private sealed record Phase3AccelerationLaneDelta(
        long FramesAccepted,
        long FramesWritten,
        long FramesReceived,
        long SendRejected,
        long QueueOverflow,
        string LastUnavailableReason);

    private sealed record Phase3PrerequisiteResult(bool IsValid, bool RequiresTuna, string Reason);

    private sealed record Phase3BenchmarkOptions(
        int RepeatCount,
        TimeSpan ProfileDuration,
        long FileTargetBytes,
        int FileWriteBytes,
        int ScreenFps,
        int ScreenKeyFrameBytes,
        int ScreenDeltaFrameBytes,
        int ListenerMaxTotalMiB,
        int ListenerMaxDurationSec,
        int ListenerAcceptTimeoutSec,
        int TunaSetupAttempts,
        double FileSendPacingMbps,
        double FileFallbackPacingMbps,
        double FileThroughputPassRatio,
        bool ScreenSmokeOnly,
        bool VerboseScreenDiagnostics)
    {
        public TimeSpan TotalLiveTimeout
            => TimeSpan.FromSeconds(Math.Max(600, RepeatCount * 2 * ProfileDuration.TotalSeconds * 3 + 900));

        public object ToArtifactModel()
            => new
            {
                repeatCount = RepeatCount,
                durationSec = (int)ProfileDuration.TotalSeconds,
                fileTargetMiB = FileTargetBytes / 1024 / 1024,
                fileWriteKiB = FileWriteBytes / 1024,
                screenFps = ScreenFps,
                screenSmokeFrameAttempts = Phase3ScreenSmokeFrameAttempts,
                listenerMaxTotalMiB = ListenerMaxTotalMiB,
                listenerMaxDurationSec = ListenerMaxDurationSec,
                listenerAcceptTimeoutSec = ListenerAcceptTimeoutSec,
                tunaSetupAttempts = TunaSetupAttempts,
                maxPriceNknPerMb = Phase3MaxPriceNknPerMb,
                fileSendPacingMbps = FileSendPacingMbps,
                fileFallbackPacingMbps = FileFallbackPacingMbps,
                fileThroughputPassRatio = FileThroughputPassRatio,
                screenSmokeOnly = ScreenSmokeOnly,
                verboseScreenDiagnostics = VerboseScreenDiagnostics,
                tunaProfileIsolation = true,
            };

        public static Phase3BenchmarkOptions Load()
            => new(
                RepeatCount: ReadInt("NLINK_PHASE3_BENCHMARK_REPEATS", 3, min: 1, max: 10),
                ProfileDuration: TimeSpan.FromSeconds(ReadInt("NLINK_PHASE3_BENCHMARK_DURATION_SEC", 60, min: 1, max: 600)),
                FileTargetBytes: ReadInt("NLINK_PHASE3_BENCHMARK_FILE_MIB", 256, min: 1, max: 4096) * 1024L * 1024L,
                FileWriteBytes: FileTransferChunkBudget.MaxRawChunkBytes,
                ScreenFps: ReadInt("NLINK_PHASE3_BENCHMARK_SCREEN_FPS", 15, min: 1, max: 30),
                ScreenKeyFrameBytes: 96 * 1024,
                ScreenDeltaFrameBytes: 18 * 1024,
                ListenerMaxTotalMiB: ReadInt("NLINK_PHASE3_BENCHMARK_LISTENER_MAX_MIB", 384, min: 1, max: 4096),
                ListenerMaxDurationSec: ReadInt("NLINK_PHASE3_BENCHMARK_LISTENER_MAX_DURATION_SEC", 180, min: 30, max: 3600),
                ListenerAcceptTimeoutSec: ReadInt("NLINK_PHASE3_BENCHMARK_LISTENER_ACCEPT_TIMEOUT_SEC", 180, min: 30, max: 3600),
                TunaSetupAttempts: ReadInt("NLINK_PHASE3_BENCHMARK_TUNA_SETUP_ATTEMPTS", 3, min: 1, max: 5),
                FileSendPacingMbps: Phase3FileSendPacingMbps,
                FileFallbackPacingMbps: Phase3FileSendPacingMbps,
                FileThroughputPassRatio: 1.25,
                ScreenSmokeOnly: ReadBool(Phase3ScreenSmokeOnlyEnv),
                VerboseScreenDiagnostics: ReadBool(Phase3VerboseScreenDiagnosticsEnv));

        private static int ReadInt(string name, int fallback, int min, int max)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? Math.Clamp(parsed, min, max)
                : fallback;
        }

        private static bool ReadBool(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class Phase3RunResult
    {
        public string Event { get; init; } = "run_summary";

        public string RunId { get; init; } = string.Empty;

        public Phase3Profile Profile { get; init; }

        public Phase3TransportMode Mode { get; init; }

        public int Repeat { get; init; }

        public long DurationMs { get; init; }

        public long BytesSent { get; init; }

        public long BytesReceived { get; init; }

        public int SentFrames { get; init; }

        public int ReceivedFrames { get; init; }

        public double SenderThroughputMbps { get; init; }

        public double ReceiverThroughputMbps { get; init; }

        public double LatencyP50Ms { get; init; } = -1;

        public double LatencyP95Ms { get; init; } = -1;

        public double LatencyP99Ms { get; init; } = -1;

        public double DropRate { get; init; }

        public int StallCount { get; init; }

        public int TunaFrameCount { get; init; }

        public int NknFrameCount { get; init; }

        public long AccelerationFramesAccepted { get; init; }

        public long AccelerationFramesWritten { get; init; }

        public long AccelerationFramesReceived { get; init; }

        public long AccelerationSendRejected { get; init; }

        public long AccelerationQueueOverflow { get; init; }

        public string AccelerationLastUnavailableReason { get; init; } = string.Empty;

        public int ScreenFragmentsAttempted { get; init; }

        public int ScreenFragmentsSendCompleted { get; init; }

        public int ScreenSendFailureCount { get; init; }

        public int ScreenAcceleratedSendCount { get; init; }

        public int ScreenTunaWriteCount { get; init; }

        public int ScreenTunaReadCount { get; init; }

        public int ScreenNknFallbackCount { get; init; }

        public int ScreenAcceleratedRejectCount { get; init; }

        public int ScreenSecureRejectCount { get; init; }

        public int ScreenFramesCompleted { get; init; }

        public bool ScreenSmokePassed { get; init; }

        public bool ScreenSmokeOnly { get; init; }

        public string ScreenFirstLossReason { get; init; } = string.Empty;

        public bool Completed { get; init; }

        public bool CapReached { get; init; }

        public bool AccelerationAvailableAtStart { get; init; }

        public bool AccelerationUnavailableAfterKill { get; init; }

        public bool FallbackDelivered { get; init; }

        public bool SessionAliveAfterFallback { get; init; }

        public string FailureReason { get; init; } = string.Empty;

        public static Phase3RunResult Failed(
            string runId,
            Phase3Profile profile,
            Phase3TransportMode mode,
            int repeat,
            string reason)
            => new()
            {
                RunId = runId,
                Profile = profile,
                Mode = mode,
                Repeat = repeat,
                FailureReason = RedactPhase3ArtifactText(reason, walletPath: null, walletPassword: null),
            };
    }

    private sealed record Phase3BenchmarkSummary(
        string Event,
        string Verdict,
        bool FilePassed,
        bool ScreenPassed,
        bool ReconnectPassed,
        double BaselineFileMedianReceiverMbps,
        double TunaFileMedianReceiverMbps,
        double FileThroughputRatio,
        double BaselineScreenP95Ms,
        double TunaScreenP95Ms,
        double BaselineScreenDropRate,
        double TunaScreenDropRate,
        int BaselineScreenMedianStalls,
        int TunaScreenMedianStalls,
        IReadOnlyList<string> Reasons,
        IReadOnlyList<Phase3RunResult> Runs)
    {
        public static Phase3BenchmarkSummary Build(IReadOnlyList<Phase3RunResult> runs, Phase3BenchmarkOptions options)
        {
            var cleanRuns = runs.Where(static run => string.IsNullOrWhiteSpace(run.FailureReason)).ToArray();
            var baselineFile = cleanRuns.Where(static run => run.Profile == Phase3Profile.File && run.Mode == Phase3TransportMode.Baseline).ToArray();
            var tunaFile = cleanRuns.Where(static run => run.Profile == Phase3Profile.File && run.Mode == Phase3TransportMode.Tuna).ToArray();
            var baselineScreen = cleanRuns.Where(static run => run.Profile == Phase3Profile.Screen && run.Mode == Phase3TransportMode.Baseline).ToArray();
            var tunaScreen = cleanRuns.Where(static run => run.Profile == Phase3Profile.Screen && run.Mode == Phase3TransportMode.Tuna).ToArray();
            var reconnect = cleanRuns.FirstOrDefault(static run => run.Profile == Phase3Profile.Reconnect && run.Mode == Phase3TransportMode.Tuna);
            var hasBaselineFileAttempt = runs.Any(static run => run.Profile == Phase3Profile.File && run.Mode == Phase3TransportMode.Baseline);
            var hasTunaFileAttempt = runs.Any(static run => run.Profile == Phase3Profile.File && run.Mode == Phase3TransportMode.Tuna);
            var hasBaselineScreenAttempt = runs.Any(static run => run.Profile == Phase3Profile.Screen && run.Mode == Phase3TransportMode.Baseline);
            var hasTunaScreenAttempt = runs.Any(static run => run.Profile == Phase3Profile.Screen && run.Mode == Phase3TransportMode.Tuna);
            var hasReconnectAttempt = runs.Any(static run => run.Profile == Phase3Profile.Reconnect && run.Mode == Phase3TransportMode.Tuna);
            var reasons = new List<string>();

            var baselineFileMedian = Median(baselineFile.Select(static run => run.ReceiverThroughputMbps));
            var tunaFileMedian = Median(tunaFile.Select(static run => run.ReceiverThroughputMbps));
            var fileRatio = baselineFileMedian <= 0 ? -1 : tunaFileMedian / baselineFileMedian;
            var baselineIncomplete = baselineFile.Count(static run => !run.Completed || run.StallCount > 0);
            var tunaClean = tunaFile.Count(static run => run.Completed && run.StallCount == 0);
            var tunaFileReadinessPassed = tunaFile.Length >= options.RepeatCount &&
                                          tunaFile.All(static run =>
                                              run.TunaFrameCount > 0 &&
                                              run.AccelerationFramesWritten > 0 &&
                                              run.AccelerationFramesReceived > 0);
            var filePassed = baselineFile.Length >= options.RepeatCount &&
                             tunaFile.Length >= options.RepeatCount &&
                             tunaFileReadinessPassed &&
                             (fileRatio >= options.FileThroughputPassRatio ||
                              (baselineIncomplete >= Math.Max(2, options.RepeatCount / 2) && tunaClean == tunaFile.Length));
            if (!tunaFileReadinessPassed)
            {
                reasons.Add("file_tuna_readiness_not_met");
            }

            if (!filePassed)
            {
                reasons.Add("file_threshold_not_met");
            }

            var baselineScreenP95 = Median(baselineScreen.Select(static run => run.LatencyP95Ms));
            var tunaScreenP95 = Median(tunaScreen.Select(static run => run.LatencyP95Ms));
            var baselineDrop = Median(baselineScreen.Select(static run => run.DropRate));
            var tunaDrop = Median(tunaScreen.Select(static run => run.DropRate));
            var baselineStalls = (int)Math.Round(Median(baselineScreen.Select(static run => (double)run.StallCount)));
            var tunaStalls = (int)Math.Round(Median(tunaScreen.Select(static run => (double)run.StallCount)));
            var tunaScreenReadinessPassed = tunaScreen.Length >= options.RepeatCount &&
                                            tunaScreen.All(static run =>
                                                run.ScreenTunaWriteCount > 0 &&
                                                run.ScreenTunaReadCount > 0 &&
                                                run.ScreenFramesCompleted > 0);
            var screenPassed = baselineScreen.Length >= options.RepeatCount &&
                               tunaScreen.Length >= options.RepeatCount &&
                               tunaScreenReadinessPassed &&
                               baselineScreenP95 >= 0 &&
                               tunaScreenP95 >= 0 &&
                               tunaScreenP95 <= baselineScreenP95 + 50 &&
                               tunaScreenP95 <= baselineScreenP95 * 1.10 &&
                               tunaDrop <= baselineDrop + 0.005 &&
                               tunaStalls <= baselineStalls;
            if (!tunaScreenReadinessPassed)
            {
                reasons.Add("screen_tuna_readiness_not_met");
            }

            foreach (var screenReason in tunaScreen
                         .Select(static run => run.ScreenFirstLossReason)
                         .Where(static reason => !string.IsNullOrWhiteSpace(reason))
                         .Distinct(StringComparer.Ordinal))
            {
                reasons.Add("screen_first_loss_" + screenReason);
            }

            if (!screenPassed)
            {
                reasons.Add("screen_regression_gate_not_met");
            }

            var reconnectPassed = reconnect is not null &&
                                  reconnect.AccelerationAvailableAtStart &&
                                  reconnect.AccelerationUnavailableAfterKill &&
                                  reconnect.FallbackDelivered &&
                                  reconnect.SessionAliveAfterFallback &&
                                  string.IsNullOrWhiteSpace(reconnect.FailureReason);
            if (!reconnectPassed)
            {
                reasons.Add("reconnect_fallback_gate_not_met");
            }

            if (runs.Any(static run => !string.IsNullOrWhiteSpace(run.FailureReason)))
            {
                reasons.Add("one_or_more_runs_failed");
            }

            var verdict = runs.Count == 0 ||
                          !hasBaselineFileAttempt ||
                          !hasTunaFileAttempt ||
                          !hasBaselineScreenAttempt ||
                          !hasTunaScreenAttempt ||
                          !hasReconnectAttempt
                ? "inconclusive"
                : filePassed && screenPassed && reconnectPassed
                    ? "pass"
                    : "fail";

            return new Phase3BenchmarkSummary(
                "phase3_summary",
                verdict,
                filePassed,
                screenPassed,
                reconnectPassed,
                baselineFileMedian,
                tunaFileMedian,
                fileRatio,
                baselineScreenP95,
                tunaScreenP95,
                baselineDrop,
                tunaDrop,
                baselineStalls,
                tunaStalls,
                reasons,
                runs);
        }
    }

    private enum Phase3TransportMode
    {
        Baseline,
        Tuna,
    }

    private enum Phase3Profile
    {
        File,
        Screen,
        Reconnect,
    }
}
