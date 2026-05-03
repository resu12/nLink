using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NLink.App.Services;
using NLink.Core;
using NLink.Core.Configuration;
using NLink.Core.Metrics;
using NLink.Core.Resources;
using NLink.Core.SessionConnect;
using NLink.Infra.DevLocal;
using NLink.Infra.Nkn;

namespace NLink.App;

internal static class BenchmarkRunner
{
    private static readonly TimeSpan CycleTimeout = TimeSpan.FromSeconds(20);
    private static readonly object NknFactoryEnvLock = new();
    private const double DefaultMemoryTolerancePercent = 5d;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (!BenchmarkRunnerOptions.TryParse(args, out var options, out var parseError))
        {
            await error.WriteLineAsync($"FAIL: {parseError}");
            return 1;
        }

        try
        {
            var started = DateTimeOffset.UtcNow;
            var metrics = new MetricsRegistry();
            var sink = new MetricsTelemetrySink(metrics);
            var bench = new BenchmarkExecution(options!, metrics, sink);
            var result = await bench.RunAsync(output, ct);

            var outDir = PrepareOutputDirectory();
            var timestamp = started.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var jsonPath = Path.Combine(outDir, $"metrics-{timestamp}.json");
            var metricsSnapshot = metrics.Snapshot();
            ReliabilityGateResult? gateResult = null;
            if (options!.ReliabilityGateEnabled)
            {
                gateResult = ReliabilityGate.Evaluate(
                    new ReliabilityGateInput(
                        Metrics: metricsSnapshot,
                        SuccessRatePercent: result.SuccessRatePercent,
                        Transport: options.Transport.ToUpperInvariant(),
                        BridgeReuseMode: options.BridgeReuseMode.ToString()),
                    options.ReliabilityGateThresholds);
            }

            var payload = new BenchmarkOutput(
                Version: GetVersion(),
                StartedUtc: started,
                CompletedUtc: DateTimeOffset.UtcNow,
                Options: options!,
                Summary: result,
                Metrics: metricsSnapshot,
                ReliabilityGate: gateResult);

            await File.WriteAllTextAsync(
                jsonPath,
                JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
                }),
                ct);

            await output.WriteLineAsync("");
            await output.WriteLineAsync("Benchmark summary");
            await output.WriteLineAsync($"  Cycles: {result.CyclesSucceeded}/{result.CyclesRequested} succeeded ({result.SuccessRatePercent:F1}%)");
            await output.WriteLineAsync($"  Connect avg/p95 (ms): {FormatNullable(result.AvgConnectMs)} / {FormatNullable(result.P95ConnectMs)}");
            await output.WriteLineAsync($"  Handshake avg/p95 (ms): {FormatNullable(result.AvgHandshakeMs)} / {FormatNullable(result.P95HandshakeMs)}");
            await output.WriteLineAsync($"  Avg attempts/success: {FormatNullable(result.AvgAttemptsPerSuccess)}");
            await output.WriteLineAsync($"  Warm start ratio: {FormatNullable(result.WarmStartRatio * 100d)}%");
            await output.WriteLineAsync($"  Managed memory start/end (bytes): {result.ManagedMemoryStartBytes} / {result.ManagedMemoryEndBytes}");
            await output.WriteLineAsync($"  Managed memory growth (%): {FormatNullable(result.ManagedMemoryGrowthPercent)}");
            await output.WriteLineAsync($"  Managed steady-state growth (%): {FormatNullable(result.ManagedSteadyStateGrowthPercent)}");
            await output.WriteLineAsync($"  Private steady-state growth (%): {FormatNullable(result.PrivateSteadyStateGrowthPercent)}");
            await output.WriteLineAsync($"  Memory samples (steady-state): {result.MemorySamplesCount}");
            await output.WriteLineAsync($"  Memory check basis: {result.MemoryCheckBasis}");
            await output.WriteLineAsync($"  Peak private / working set (bytes): {result.PeakPrivateBytes} / {result.PeakWorkingSetBytes}");
            if (gateResult is not null)
            {
                await output.WriteLineAsync($"  Reliability gate: {(gateResult.Passed ? "PASS" : "FAIL")}");
            }
            if (result.MemoryCheckEnabled)
            {
                await output.WriteLineAsync($"  Memory check (tolerance ±{result.MemoryTolerancePercent:F1}%): {(result.MemoryCheckPassed ? "PASS" : "FAIL")}");
            }
            if (result.TopFailureCategories.Length == 0)
            {
                await output.WriteLineAsync("  Top failures: (none)");
            }
            else
            {
                await output.WriteLineAsync("  Top failures:");
                foreach (var failure in result.TopFailureCategories)
                {
                    await output.WriteLineAsync($"    - {failure.Category}: {failure.Count}");
                }
            }

            await output.WriteLineAsync("");
            await output.WriteLineAsync($"Metrics JSON: {Path.GetFullPath(jsonPath)}");
            if (gateResult is not null && !gateResult.Passed)
            {
                await error.WriteLineAsync("FAIL: Reliability gate failed.");
                foreach (var failure in gateResult.Failures)
                {
                    await error.WriteLineAsync($"  - [{failure.Code}] {failure.Message}");
                }
                return 3;
            }
            if (result.MemoryCheckEnabled && !result.MemoryCheckPassed)
            {
                await error.WriteLineAsync(
                    $"FAIL: Memory growth check failed ({result.MemoryCheckBasis}; managed_steady={FormatNullable(result.ManagedSteadyStateGrowthPercent)}%, private_steady={FormatNullable(result.PrivateSteadyStateGrowthPercent)}%, tolerance={result.MemoryTolerancePercent:F1}%).");
                return 2;
            }
            return 0;
        }
        catch (Exception ex)
        {
            await error.WriteLineAsync($"FAIL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    internal static bool TryParseOptionsForTests(string[] args, out BenchmarkRunnerOptions? options, out string error)
    {
        return BenchmarkRunnerOptions.TryParse(args, out options, out error);
    }

    internal static string BuildDevLocalBenchmarkPeerAddressForTests(string roleLabel, int cycleIndex)
    {
        return BenchmarkExecution.BuildDevLocalBenchmarkPeerAddress(roleLabel, cycleIndex);
    }

    internal static (string Token, ValidatedInviteV1 Invite) CreateInviteForTargetForTests(
        PeerAddress targetAddress,
        PeerAddress? boundHelperAddress = null)
    {
        return BenchmarkExecution.CreateInviteForTarget(targetAddress, boundHelperAddress);
    }

    private static string GetVersion()
    {
        var info = typeof(Program).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info))
        {
            return typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        }

        var plus = info.IndexOf('+');
        return plus > 0 ? info[..plus] : info;
    }

    private static string PrepareOutputDirectory()
    {
        var dir = Path.Combine(Environment.CurrentDirectory, "artifacts", "bench");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string FormatNullable(double? value)
    {
        return value.HasValue ? value.Value.ToString("F1", CultureInfo.InvariantCulture) : "n/a";
    }

    internal sealed record BenchmarkRunnerOptions(
        int Cycles,
        int DelayMs,
        string Transport,
        BridgeReuseMode BridgeReuseMode,
        bool MemoryCheck,
        double MemoryTolerancePercent,
        bool ReliabilityGateEnabled,
        ReliabilityGateThresholds ReliabilityGateThresholds)
    {
        public static bool TryParse(string[] args, out BenchmarkRunnerOptions? options, out string error)
        {
            options = null;
            error = string.Empty;

            var cycles = 50;
            var delayMs = 0;
            var transport = "devlocal";
            var reuse = BridgeReuseMode.PerSession;
            var memoryCheck = false;
            var memoryTolerancePercent = DefaultMemoryTolerancePercent;
            var reliabilityGateEnabled = false;
            var gateMinSuccessRatePercent = double.NaN;
            var gateRequireNoUnknown = true;
            var gateRequireNoStuck = true;
            bool? gateFailOnBridgeCrash = null;

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (!arg.StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                string key;
                string? value = null;
                var eq = arg.IndexOf('=');
                if (eq > 0)
                {
                    key = arg[..eq];
                    value = arg[(eq + 1)..];
                }
                else
                {
                    key = arg;
                    if (key is "--bench")
                    {
                        continue;
                    }

                    if (key is "--memory-check")
                    {
                        value = null;
                    }
                    else if (i + 1 < args.Length)
                    {
                        value = args[++i];
                    }
                }

                switch (key.ToLowerInvariant())
                {
                    case "--memory-check":
                        memoryCheck = true;
                        if (!string.IsNullOrWhiteSpace(value) &&
                            bool.TryParse(value, out var parsedMemoryCheck))
                        {
                            memoryCheck = parsedMemoryCheck;
                        }
                        break;
                    case "--reliability-gate":
                        reliabilityGateEnabled = true;
                        if (!string.IsNullOrWhiteSpace(value) &&
                            bool.TryParse(value, out var parsedGate))
                        {
                            reliabilityGateEnabled = parsedGate;
                        }
                        break;
                    case "--gate-min-success-rate":
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out gateMinSuccessRatePercent) ||
                            double.IsNaN(gateMinSuccessRatePercent) ||
                            double.IsInfinity(gateMinSuccessRatePercent))
                        {
                            error = "Invalid --gate-min-success-rate value.";
                            return false;
                        }
                        break;
                    case "--gate-no-unknown":
                        if (!bool.TryParse(value, out gateRequireNoUnknown))
                        {
                            error = "Invalid --gate-no-unknown value.";
                            return false;
                        }
                        break;
                    case "--gate-no-stuck":
                        if (!bool.TryParse(value, out gateRequireNoStuck))
                        {
                            error = "Invalid --gate-no-stuck value.";
                            return false;
                        }
                        break;
                    case "--gate-fail-on-bridge-crash":
                        if (!bool.TryParse(value, out var parsedGateBridgeCrash))
                        {
                            error = "Invalid --gate-fail-on-bridge-crash value.";
                            return false;
                        }
                        gateFailOnBridgeCrash = parsedGateBridgeCrash;
                        break;
                    case "--memory-tolerance-percent":
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out memoryTolerancePercent) ||
                            double.IsNaN(memoryTolerancePercent) ||
                            double.IsInfinity(memoryTolerancePercent) ||
                            memoryTolerancePercent < 0)
                        {
                            error = "Invalid --memory-tolerance-percent value.";
                            return false;
                        }
                        break;
                    case "--cycles":
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out cycles) || cycles <= 0)
                        {
                            error = "Invalid --cycles value.";
                            return false;
                        }
                        break;
                    case "--delay-ms":
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out delayMs) || delayMs < 0)
                        {
                            error = "Invalid --delay-ms value.";
                            return false;
                        }
                        break;
                    case "--transport":
                        transport = (value ?? string.Empty).Trim().ToLowerInvariant();
                        if (transport is not ("devlocal" or "nkn"))
                        {
                            error = "Invalid --transport value. Use devlocal or nkn.";
                            return false;
                        }
                        break;
                    case "--bridge-reuse-mode":
                        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
                        if (normalized == "persession")
                        {
                            reuse = BridgeReuseMode.PerSession;
                        }
                        else if (normalized == "keepalive")
                        {
                            reuse = BridgeReuseMode.KeepAlive;
                        }
                        else
                        {
                            error = "Invalid --bridge-reuse-mode value. Use persession or keepalive.";
                            return false;
                        }
                        break;
                }
            }

            var resolvedGateMinSuccessRate = double.IsNaN(gateMinSuccessRatePercent)
                ? (transport == "devlocal" ? 100d : 95d)
                : gateMinSuccessRatePercent;
            var resolvedGateFailOnBridgeCrash = gateFailOnBridgeCrash ?? (transport == "nkn");
            var gateThresholds = new ReliabilityGateThresholds(
                MinSuccessRatePercent: resolvedGateMinSuccessRate,
                RequireNoUnknownFailures: gateRequireNoUnknown,
                RequireNoStuckStates: gateRequireNoStuck,
                FailOnBridgeCrash: resolvedGateFailOnBridgeCrash);

            options = new BenchmarkRunnerOptions(
                cycles,
                delayMs,
                transport,
                reuse,
                memoryCheck,
                memoryTolerancePercent,
                reliabilityGateEnabled,
                gateThresholds);
            return true;
        }
    }

    private sealed class BenchmarkExecution
    {
        private readonly BenchmarkRunnerOptions options;
        private readonly MetricsRegistry metrics;
        private readonly ITransportTelemetrySink sink;
        private readonly Dictionary<string, int> failures = new(StringComparer.Ordinal);

        public BenchmarkExecution(BenchmarkRunnerOptions options, MetricsRegistry metrics, ITransportTelemetrySink sink)
        {
            this.options = options;
            this.metrics = metrics;
            this.sink = sink;
        }

        public async Task<BenchmarkSummary> RunAsync(TextWriter output, CancellationToken ct)
        {
            var success = 0;
            long peakPrivateBytes = 0;
            long peakWorkingSetBytes = 0;

            BenchmarkSessionPair? sharedPair = null;
            if (options.BridgeReuseMode == BridgeReuseMode.KeepAlive)
            {
                sharedPair = CreateSessionPair(cycleSeed: 0);
            }

            try
            {
                if (options.MemoryCheck)
                {
                    await output.WriteLineAsync("Warmup cycle (excluded from memory baseline)...");
                    _ = await RunCycleAsync(0, sharedPair, ct);
                }

                var memorySamples = options.MemoryCheck ? new List<MemorySample>(64) : null;
                var initialManagedBytes = CaptureManagedBytes();
                var memoryBaselineCycle = options.MemoryCheck
                    ? Math.Max(10, Math.Min(100, options.Cycles / 10))
                    : 0;
                var memorySampleEveryCycles = options.MemoryCheck
                    ? Math.Max(1, Math.Min(50, options.Cycles / 20))
                    : 0;
                SampleProcessMemory(ref peakPrivateBytes, ref peakWorkingSetBytes);

                for (var i = 0; i < options.Cycles; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var cycleIndex = i + 1;
                    await output.WriteLineAsync($"Cycle {cycleIndex}/{options.Cycles}...");
                    var ok = await RunCycleAsync(cycleIndex, sharedPair, ct);
                    if (ok)
                    {
                        success++;
                    }

                    if (options.MemoryCheck && cycleIndex == memoryBaselineCycle)
                    {
                        initialManagedBytes = CaptureManagedBytes();
                        memorySamples!.Clear();
                    }

                    if (options.MemoryCheck &&
                        cycleIndex >= memoryBaselineCycle &&
                        (cycleIndex == options.Cycles ||
                         cycleIndex == memoryBaselineCycle ||
                         ((cycleIndex - memoryBaselineCycle) % memorySampleEveryCycles == 0)))
                    {
                        memorySamples!.Add(CaptureMemorySample(cycleIndex));
                    }

                    SampleProcessMemory(ref peakPrivateBytes, ref peakWorkingSetBytes);

                    if (options.DelayMs > 0 && i < options.Cycles - 1)
                    {
                        await Task.Delay(options.DelayMs, ct);
                    }
                }

                var finalManagedBytes = CaptureManagedBytes();
                SampleProcessMemory(ref peakPrivateBytes, ref peakWorkingSetBytes);

                var snapshot = metrics.Snapshot();
                var connectHist = snapshot.Histograms.Where(h => h.Name == "transport_connect_duration_ms" && h.Tags.Result == "success").ToArray();
                var handshakeHist = snapshot.Histograms.Where(h => h.Name == "transport_handshake_duration_ms" && h.Tags.Result == "success").ToArray();
                var warmRatioGauge = snapshot.Gauges.FirstOrDefault(g => g.Name == "bridge_warm_start_ratio" && MatchesMode(g.Tags.BridgeReuseMode));
                var connectAttempts = snapshot.Counters
                    .Where(c => c.Name == "transport_connect_attempts_total" && MatchesMode(c.Tags.BridgeReuseMode))
                    .Sum(c => c.Value);
                var connectSuccesses = snapshot.Counters
                    .Where(c => c.Name == "transport_connect_success_total" && MatchesMode(c.Tags.BridgeReuseMode))
                    .Sum(c => c.Value);
                var avgAttemptsPerSuccess = connectSuccesses > 0 ? (double?)connectAttempts / connectSuccesses : null;
                var managedGrowthPercent = CalculateGrowthPercent(initialManagedBytes, finalManagedBytes);
                var steadyStateAnalysis = AnalyzeSteadyStateMemory(memorySamples);
                var memoryCheckBasis = DetermineMemoryCheckBasis(steadyStateAnalysis);
                var memoryCheckPassed = !options.MemoryCheck || IsMemoryWithinTolerance(steadyStateAnalysis, options.MemoryTolerancePercent, managedGrowthPercent);

                return new BenchmarkSummary(
                    CyclesRequested: options.Cycles,
                    CyclesSucceeded: success,
                    SuccessRatePercent: options.Cycles > 0 ? (100d * success / options.Cycles) : 0,
                    AvgConnectMs: MergeAverage(connectHist),
                    P95ConnectMs: MergePercentile(connectHist, 0.95),
                    AvgHandshakeMs: MergeAverage(handshakeHist),
                    P95HandshakeMs: MergePercentile(handshakeHist, 0.95),
                    AvgAttemptsPerSuccess: avgAttemptsPerSuccess,
                    WarmStartRatio: warmRatioGauge?.Value,
                    ManagedMemoryStartBytes: initialManagedBytes,
                    ManagedMemoryEndBytes: finalManagedBytes,
                    ManagedMemoryGrowthPercent: managedGrowthPercent,
                    ManagedSteadyStateGrowthPercent: steadyStateAnalysis.ManagedGrowthPercent,
                    PrivateSteadyStateGrowthPercent: steadyStateAnalysis.PrivateGrowthPercent,
                    MemorySamplesCount: steadyStateAnalysis.SampleCount,
                    MemoryCheckBasis: memoryCheckBasis,
                    PeakPrivateBytes: peakPrivateBytes,
                    PeakWorkingSetBytes: peakWorkingSetBytes,
                    MemoryCheckEnabled: options.MemoryCheck,
                    MemoryTolerancePercent: options.MemoryTolerancePercent,
                    MemoryCheckPassed: memoryCheckPassed,
                    FinalActiveCounters: ActiveRuntimeCounters.Snapshot(),
                    MaxActiveConnectAttempts: ActiveRuntimeCounters.MaxActiveConnectAttemptsObserved(),
                    TopFailureCategories: failures
                        .OrderByDescending(kv => kv.Value)
                        .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                        .Take(5)
                        .Select(kv => new FailureCount(kv.Key, kv.Value))
                        .ToArray());
            }
            finally
            {
                sharedPair?.Dispose();
            }
        }

        private async Task<bool> RunCycleAsync(int cycleIndex, BenchmarkSessionPair? sharedPair, CancellationToken ct)
        {
            var pair = sharedPair ?? CreateSessionPair(cycleIndex);
            using var ownedPair = sharedPair is null ? pair : null;
            var helpee = pair.Helpee;
            var helper = pair.Helper;
            using var cycleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cycleCts.CancelAfter(CycleTimeout);

            var helperConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var helpeeConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var incomingJoin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var helperDisconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var helpeeDisconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            EventHandler incomingJoinHandler = (_, _) => incomingJoin.TrySetResult();
            EventHandler helperDisconnectedHandler = (_, _) => helperDisconnected.TrySetResult();
            EventHandler helpeeDisconnectedHandler = (_, _) => helpeeDisconnected.TrySetResult();
            EventHandler<SessionRuntimeStateChangedEventArgs> helpeeStateChangedHandler = (_, e) =>
            {
                if (e.State == SessionRuntimeState.Connected)
                {
                    helpeeConnected.TrySetResult();
                }
            };
            EventHandler<SessionRuntimeStateChangedEventArgs> helperStateChangedHandler = (_, e) =>
            {
                if (e.State == SessionRuntimeState.Connected)
                {
                    helperConnected.TrySetResult();
                }
            };

            helpee.IncomingJoinRequestAvailable += incomingJoinHandler;
            helper.Disconnected += helperDisconnectedHandler;
            helpee.Disconnected += helpeeDisconnectedHandler;
            helpee.StateChanged += helpeeStateChangedHandler;
            helper.StateChanged += helperStateChangedHandler;

            try
            {
                await helpee.StartHelpeeAsync(cycleCts.Token);
                var (inviteToken, invite) = CreateInviteForTarget(
                    GetHostedAddressOrThrow(helpee),
                    pair.HelperInviteBindingAddress);
                await helper.StartHelperAsync(inviteToken, invite, cycleCts.Token);

                await incomingJoin.Task.WaitAsync(TimeSpan.FromSeconds(10), cycleCts.Token);
                await helpee.ApproveAsync(cycleCts.Token);

                await Task.WhenAll(
                    helperConnected.Task.WaitAsync(TimeSpan.FromSeconds(10), cycleCts.Token),
                    helpeeConnected.Task.WaitAsync(TimeSpan.FromSeconds(10), cycleCts.Token));

                await helper.TrySendChatTextAsync("bench", cycleCts.Token);

                await helper.DisconnectAsync();
                await Task.WhenAny(
                    helpeeDisconnected.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None),
                    Task.Delay(200));
                await helpee.ResetAsync();
                if (options.Transport == "nkn" && options.BridgeReuseMode == BridgeReuseMode.KeepAlive)
                {
                    await Task.Delay(150, ct);
                }
                return true;
            }
            catch (Exception)
            {
                RecordFailure(helper.GetDiagnosticsSnapshot());
                RecordFailure(helpee.GetDiagnosticsSnapshot());
                try { await helper.ResetAsync(); } catch { }
                try { await helpee.ResetAsync(); } catch { }
                return false;
            }
            finally
            {
                helpee.IncomingJoinRequestAvailable -= incomingJoinHandler;
                helper.Disconnected -= helperDisconnectedHandler;
                helpee.Disconnected -= helpeeDisconnectedHandler;
                helpee.StateChanged -= helpeeStateChangedHandler;
                helper.StateChanged -= helperStateChangedHandler;
            }
        }

        private static PeerAddress GetHostedAddressOrThrow(SessionRuntime runtime)
        {
            if (runtime.CurrentLocalPeerAddress is PeerAddress address)
            {
                return address;
            }

            throw new InvalidOperationException("Active helpee transport did not expose a local peer address.");
        }

        internal static (string Token, ValidatedInviteV1 Invite) CreateInviteForTarget(
            PeerAddress targetAddress,
            PeerAddress? boundHelperAddress = null)
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var factory = InviteTokenServiceFactory.CreateInviteTokenFactory();
            var create = factory.Create(
                new InviteTokenCreateRequest(
                    IssuerAddress: targetAddress,
                    TargetAddress: targetAddress,
                    SessionId: new SessionId($"sess_bench_{Guid.NewGuid():N}"),
                    Capabilities: InviteCapabilities.Chat | InviteCapabilities.ScreenShare | InviteCapabilities.RemoteControl | InviteCapabilities.FileTransfer,
                    Lifetime: TimeSpan.FromMinutes(5),
                    BoundHelperAddress: boundHelperAddress),
                nowUtc);
            if (!create.IsSuccess || string.IsNullOrWhiteSpace(create.Token))
            {
                throw new InvalidOperationException(create.Message ?? "Failed to create benchmark invite.");
            }

            var validator = InviteTokenServiceFactory.CreateInviteTokenValidator();
            var validation = validator.Validate(create.Token, nowUtc.AddSeconds(1));
            if (!validation.IsSuccess || validation.Invite is null)
            {
                throw new InvalidOperationException(validation.Message ?? "Failed to validate benchmark invite.");
            }

            return (create.Token, validation.Invite);
        }

        private BenchmarkSessionPair CreateSessionPair(int cycleSeed)
        {
            var reusePolicy = new BridgeReusePolicy(options.BridgeReuseMode, TimeSpan.FromSeconds(60));
            var helpeeDevLocalAddress = options.Transport == "devlocal"
                ? new PeerAddress(BuildDevLocalBenchmarkPeerAddress("helpee", cycleSeed))
                : (PeerAddress?)null;
            var helperDevLocalAddress = options.Transport == "devlocal"
                ? new PeerAddress(BuildDevLocalBenchmarkPeerAddress("helper", cycleSeed))
                : (PeerAddress?)null;
            return new BenchmarkSessionPair(
                new SessionRuntime(CreateTransportFactory("helpee", cycleSeed), watchdogOptions: null, watchdogDelayAsync: null, telemetrySink: sink, bridgeReusePolicy: reusePolicy),
                new SessionRuntime(CreateTransportFactory("helper", cycleSeed), watchdogOptions: null, watchdogDelayAsync: null, telemetrySink: sink, bridgeReusePolicy: reusePolicy),
                helperDevLocalAddress);
        }

        private Func<ISignalingTransport> CreateTransportFactory(string roleLabel, int cycleIndex)
        {
            return options.Transport switch
            {
                "nkn" => () => CreateNknBenchmarkTransport(roleLabel, cycleIndex),
                _ => () => new DevLocalTransport(BuildDevLocalBenchmarkPeerAddress(roleLabel, cycleIndex)),
            };
        }

        internal static string BuildDevLocalBenchmarkPeerAddress(string roleLabel, int cycleIndex)
        {
            if (string.IsNullOrWhiteSpace(roleLabel))
            {
                throw new ArgumentException("Role label is required.", nameof(roleLabel));
            }

            return $"bench.devlocal.{roleLabel.Trim().ToLowerInvariant()}.{cycleIndex:D4}";
        }

        private static ISignalingTransport CreateNknBenchmarkTransport(string roleLabel, int cycleIndex)
        {
            lock (NknFactoryEnvLock)
            {
                var prevKeyPath = Environment.GetEnvironmentVariable("NLINK_NKN_KEY_PATH");
                var prevIdentifier = Environment.GetEnvironmentVariable("NLINK_NKN_IDENTIFIER");
                var prevUnsafeDeveloperMode = Environment.GetEnvironmentVariable(ReleaseOverridePolicy.UnsafeDeveloperModeEnvVar);
                try
                {
                    var tempDir = Path.Combine(Path.GetTempPath(), "nlink-bench-identities");
                    Directory.CreateDirectory(tempDir);
                    var keyPath = Path.Combine(tempDir, $"identity-{roleLabel}-{cycleIndex:D4}.json");
                    var identifier = $"nlink-bench-{roleLabel}-{cycleIndex:D4}";
                    Environment.SetEnvironmentVariable(ReleaseOverridePolicy.UnsafeDeveloperModeEnvVar, "1");
                    Environment.SetEnvironmentVariable("NLINK_NKN_KEY_PATH", keyPath);
                    Environment.SetEnvironmentVariable("NLINK_NKN_IDENTIFIER", identifier);
                    return new NknSignalingTransport();
                }
                finally
                {
                    Environment.SetEnvironmentVariable("NLINK_NKN_KEY_PATH", prevKeyPath);
                    Environment.SetEnvironmentVariable("NLINK_NKN_IDENTIFIER", prevIdentifier);
                    Environment.SetEnvironmentVariable(ReleaseOverridePolicy.UnsafeDeveloperModeEnvVar, prevUnsafeDeveloperMode);
                }
            }
        }

        private void RecordFailure(DiagnosticsSnapshot snapshot)
        {
            var category = string.IsNullOrWhiteSpace(snapshot.LastFailureCategory) ? "(none)" : snapshot.LastFailureCategory;
            if (category == "(none)")
            {
                return;
            }

            failures.TryGetValue(category, out var count);
            failures[category] = count + 1;
        }

        private bool MatchesMode(string bridgeReuseMode)
        {
            return string.Equals(
                string.IsNullOrWhiteSpace(bridgeReuseMode) ? "PerSession" : bridgeReuseMode,
                options.BridgeReuseMode.ToString(),
                StringComparison.OrdinalIgnoreCase);
        }

        private static long CaptureManagedBytes()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return GC.GetTotalMemory(forceFullCollection: true);
        }

        private static void SampleProcessMemory(ref long peakPrivateBytes, ref long peakWorkingSetBytes)
        {
            using var process = Process.GetCurrentProcess();
            if (process.PrivateMemorySize64 > peakPrivateBytes)
            {
                peakPrivateBytes = process.PrivateMemorySize64;
            }

            if (process.WorkingSet64 > peakWorkingSetBytes)
            {
                peakWorkingSetBytes = process.WorkingSet64;
            }
        }

        private static double? CalculateGrowthPercent(long startBytes, long endBytes)
        {
            if (startBytes <= 0)
            {
                return null;
            }

            return ((double)(endBytes - startBytes) / startBytes) * 100d;
        }

        private static MemorySample CaptureMemorySample(int cycleIndex)
        {
            var managedBytes = CaptureManagedBytes();
            using var process = Process.GetCurrentProcess();
            return new MemorySample(
                cycleIndex,
                managedBytes,
                process.PrivateMemorySize64,
                process.WorkingSet64);
        }

        private static SteadyStateMemoryAnalysis AnalyzeSteadyStateMemory(List<MemorySample>? samples)
        {
            if (samples is null || samples.Count < 6)
            {
                return new SteadyStateMemoryAnalysis(null, null, samples?.Count ?? 0);
            }

            var ordered = samples
                .OrderBy(s => s.CycleIndex)
                .ToArray();
            var windowSize = Math.Max(3, Math.Min(10, ordered.Length / 3));
            var firstWindow = ordered.Take(windowSize).ToArray();
            var lastWindow = ordered.Skip(Math.Max(0, ordered.Length - windowSize)).ToArray();

            var firstManaged = Median(firstWindow.Select(s => s.ManagedBytes));
            var lastManaged = Median(lastWindow.Select(s => s.ManagedBytes));
            var firstPrivate = Median(firstWindow.Select(s => s.PrivateBytes));
            var lastPrivate = Median(lastWindow.Select(s => s.PrivateBytes));

            return new SteadyStateMemoryAnalysis(
                CalculateGrowthPercent(firstManaged, lastManaged),
                CalculateGrowthPercent(firstPrivate, lastPrivate),
                ordered.Length);
        }

        private static bool IsMemoryWithinTolerance(SteadyStateMemoryAnalysis analysis, double tolerancePercent, double? fallbackManagedGrowthPercent)
        {
            if (analysis.PrivateGrowthPercent.HasValue)
            {
                // Primary leak gate uses steady-state process private bytes (more stable than managed GC heap size).
                return analysis.PrivateGrowthPercent.Value <= tolerancePercent;
            }

            if (analysis.ManagedGrowthPercent.HasValue)
            {
                return analysis.ManagedGrowthPercent.Value <= tolerancePercent;
            }

            return !fallbackManagedGrowthPercent.HasValue || fallbackManagedGrowthPercent.Value <= tolerancePercent;
        }

        private static string DetermineMemoryCheckBasis(SteadyStateMemoryAnalysis analysis)
        {
            if (analysis.PrivateGrowthPercent.HasValue)
            {
                return "steady-state private bytes";
            }

            if (analysis.ManagedGrowthPercent.HasValue)
            {
                return "steady-state managed bytes (fallback)";
            }

            return "managed bytes start/end (fallback)";
        }

        private static long Median(IEnumerable<long> values)
        {
            var sorted = values.OrderBy(v => v).ToArray();
            if (sorted.Length == 0)
            {
                return 0;
            }

            var mid = sorted.Length / 2;
            if ((sorted.Length & 1) == 1)
            {
                return sorted[mid];
            }

            return (sorted[mid - 1] + sorted[mid]) / 2;
        }

        private static double? MergeAverage(HistogramMetricSnapshot[] histograms)
        {
            if (histograms.Length == 0)
            {
                return null;
            }

            var count = histograms.Sum(h => h.Count);
            if (count <= 0)
            {
                return null;
            }

            var sum = histograms.Sum(h => h.Sum);
            return sum / count;
        }

        private static double? MergePercentile(HistogramMetricSnapshot[] histograms, double percentile)
        {
            if (histograms.Length == 0)
            {
                return null;
            }

            var merged = new SortedDictionary<double, long>();
            long totalCount = 0;

            foreach (var hist in histograms)
            {
                foreach (var bucket in hist.Buckets)
                {
                    if (!merged.TryGetValue(bucket.UpperBound, out var count))
                    {
                        count = 0;
                    }
                    merged[bucket.UpperBound] = count + bucket.Count;
                }
                totalCount += hist.Count;
            }

            if (totalCount <= 0)
            {
                return null;
            }

            var target = Math.Max(1L, (long)Math.Ceiling(totalCount * percentile));
            long cumulative = 0;
            foreach (var pair in merged)
            {
                cumulative += pair.Value;
                if (cumulative >= target)
                {
                    return double.IsPositiveInfinity(pair.Key) ? merged.Keys.Where(k => !double.IsPositiveInfinity(k)).DefaultIfEmpty(0d).Last() : pair.Key;
                }
            }

            return null;
        }
    }

    private sealed record BenchmarkOutput(
        string Version,
        DateTimeOffset StartedUtc,
        DateTimeOffset CompletedUtc,
        BenchmarkRunnerOptions Options,
        BenchmarkSummary Summary,
        MetricsSnapshot Metrics,
        ReliabilityGateResult? ReliabilityGate);

    private sealed record BenchmarkSummary(
        int CyclesRequested,
        int CyclesSucceeded,
        double SuccessRatePercent,
        double? AvgConnectMs,
        double? P95ConnectMs,
        double? AvgHandshakeMs,
        double? P95HandshakeMs,
        double? AvgAttemptsPerSuccess,
        double? WarmStartRatio,
        long ManagedMemoryStartBytes,
        long ManagedMemoryEndBytes,
        double? ManagedMemoryGrowthPercent,
        double? ManagedSteadyStateGrowthPercent,
        double? PrivateSteadyStateGrowthPercent,
        int MemorySamplesCount,
        long PeakPrivateBytes,
        long PeakWorkingSetBytes,
        bool MemoryCheckEnabled,
        double MemoryTolerancePercent,
        bool MemoryCheckPassed,
        string MemoryCheckBasis,
        ActiveResourceCountersSnapshot FinalActiveCounters,
        long MaxActiveConnectAttempts,
        FailureCount[] TopFailureCategories);

    private sealed record FailureCount(string Category, int Count);
    private sealed record MemorySample(int CycleIndex, long ManagedBytes, long PrivateBytes, long WorkingSetBytes);
    private sealed record SteadyStateMemoryAnalysis(double? ManagedGrowthPercent, double? PrivateGrowthPercent, int SampleCount);

    private sealed class BenchmarkSessionPair : IDisposable
    {
        public BenchmarkSessionPair(SessionRuntime helpee, SessionRuntime helper, PeerAddress? helperInviteBindingAddress)
        {
            Helpee = helpee;
            Helper = helper;
            HelperInviteBindingAddress = helperInviteBindingAddress;
        }

        public SessionRuntime Helpee { get; }

        public SessionRuntime Helper { get; }

        public PeerAddress? HelperInviteBindingAddress { get; }

        public void Dispose()
        {
            Helper.Dispose();
            Helpee.Dispose();
        }
    }
}
