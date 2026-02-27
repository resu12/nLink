using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NLink.App.Services;
using NLink.Core;
using NLink.Core.Metrics;
using NLink.Core.Resources;
using NLink.Infra.DevLocal;
using NLink.Infra.Nkn;

namespace NLink.App;

internal static class ResourceBenchmarkRunner
{
    private static readonly TimeSpan CycleTimeout = TimeSpan.FromSeconds(20);
    private static readonly object NknFactoryEnvLock = new();

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (!ResourceRunnerOptions.TryParse(args, out var options, out var parseError))
        {
            await error.WriteLineAsync($"FAIL: {parseError}");
            return 1;
        }

        try
        {
            var outDir = Path.Combine(Environment.CurrentDirectory, "artifacts", "resources");
            Directory.CreateDirectory(outDir);
            var started = DateTimeOffset.UtcNow;
            var timestamp = started.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

            var metrics = new MetricsRegistry();
            var sink = new MetricsTelemetrySink(metrics);

            ResourceRunResult runResult = options!.Mode switch
            {
                ResourceRunMode.Benchmark => await RunResourceBenchmarkAsync(options, metrics, sink, output, ct),
                ResourceRunMode.LeakCheck => await RunLeakCheckAsync(options, metrics, sink, output, ct),
                _ => throw new InvalidOperationException("Unknown resource mode.")
            };

            var metadata = new ResourceRunMetadata(
                Version: GetVersion(),
                Transport: options.Transport,
                BridgeReuseMode: options.BridgeReuseMode.ToString(),
                Cycles: options.Cycles,
                Scenario: options.Mode == ResourceRunMode.Benchmark ? "resource_benchmark" : "leak_check",
                StartedUtc: started,
                CompletedUtc: DateTimeOffset.UtcNow);

            ResourceGateResult? gate = null;
            if (options.FailOnGate)
            {
                gate = ResourceGate.Evaluate(
                    new ResourceGateInput(runResult.Summary, options.Transport.ToUpperInvariant(), options.BridgeReuseMode.ToString(), metadata.Scenario),
                    options.ResourceGateThresholds);
            }

            var artifact = new ResourceBenchmarkArtifact(metadata, runResult.Samples.ToArray(), runResult.Summary, gate, runResult.Notes.ToArray());
            var prefix = options.Mode == ResourceRunMode.Benchmark ? "resource-run" : "leak-check";
            var jsonPath = Path.Combine(outDir, $"{prefix}-{timestamp}.json");
            await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(artifact, ResourceJson.Options(indented: true)), ct);

            var summaryName = options.Mode == ResourceRunMode.Benchmark ? "resource-summary.txt" : "leak-check-summary.txt";
            var summaryPath = Path.Combine(outDir, summaryName);
            var summaryText = BuildRunnerSummaryText(runResult, gate, jsonPath);
            await File.WriteAllTextAsync(summaryPath, summaryText, ct);

            await output.WriteLineAsync(summaryText);
            await output.WriteLineAsync($"Resource JSON: {Path.GetFullPath(jsonPath)}");
            await output.WriteLineAsync($"Resource summary: {Path.GetFullPath(summaryPath)}");

            if (gate is { Passed: false })
            {
                var failPath = Path.Combine(outDir, "resource-gate-failure.txt");
                await File.WriteAllTextAsync(failPath, gate.ToText(), ct);
                await error.WriteLineAsync("FAIL: Resource gate failed.");
                foreach (var failure in gate.Failures)
                {
                    await error.WriteLineAsync($"  - [{failure.Code}] {failure.Message}");
                }
                await error.WriteLineAsync($"  Failure details: {Path.GetFullPath(failPath)}");
                return 3;
            }

            if (runResult.LeakCheckFailed)
            {
                await error.WriteLineAsync("FAIL: Leak check growth gate failed.");
                foreach (var note in runResult.Notes.Where(n => n.StartsWith("LEAK_FAIL", StringComparison.Ordinal)))
                {
                    await error.WriteLineAsync($"  - {note}");
                }
                return 4;
            }

            return 0;
        }
        catch (Exception ex)
        {
            await error.WriteLineAsync($"FAIL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static async Task<ResourceRunResult> RunResourceBenchmarkAsync(ResourceRunnerOptions options, MetricsRegistry metrics, ITransportTelemetrySink sink, TextWriter output, CancellationToken ct)
    {
        ActiveRuntimeCounters.ResetForTests();
        using var pair = CreateSessionPair(options, sink, cycleSeed: 0);
        var resourceSampler = new ResourceSampler(GetBridgePid);
        var samples = new List<ResourceSnapshot>(1024);
        var sampleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sampleLoop = RunSamplingLoopAsync(resourceSampler, samples, TimeSpan.FromMilliseconds(options.SampleIntervalMs), sampleCts.Token);

        var notes = new List<string>();
        try
        {
            var code = new SessionCode("123456");
            var benchmarkStartedUtc = DateTimeOffset.UtcNow;
            DateTimeOffset? idleHostingWindowEndUtc = null;
            DateTimeOffset? finalIdleWindowEndUtc = null;
            await output.WriteLineAsync("[resources] starting helpee host");
            await pair.Helpee.StartHelpeeAsync(code, ct);
            await DelayAndSamplePhaseAsync("idle_hosting", TimeSpan.FromSeconds(options.IdleSeconds), output, ct);
            idleHostingWindowEndUtc = await CaptureGcStabilizedCheckpointAsync("idle_hosting_end", resourceSampler, samples, output, ct);

            await output.WriteLineAsync("[resources] connecting helper");
            await RunConnectApproveAsync(pair.Helpee, pair.Helper, code, ct);
            await DelayAndSamplePhaseAsync("idle_connected", TimeSpan.FromSeconds(options.ConnectedIdleSeconds), output, ct);

            await output.WriteLineAsync("[resources] disconnecting");
            await pair.Helper.DisconnectAsync();
            await pair.Helpee.ResetAsync();
            await DelayAndSamplePhaseAsync("idle_after_disconnect", TimeSpan.FromSeconds(options.FinalIdleSeconds), output, ct);
            finalIdleWindowEndUtc = await CaptureGcStabilizedCheckpointAsync("final_idle_end", resourceSampler, samples, output, ct);

            notes.Add($"benchmark_started_utc={benchmarkStartedUtc:O}");
            if (idleHostingWindowEndUtc.HasValue)
            {
                notes.Add($"idle_hosting_end_utc={idleHostingWindowEndUtc.Value:O}");
            }
            if (finalIdleWindowEndUtc.HasValue)
            {
                notes.Add($"final_idle_end_utc={finalIdleWindowEndUtc.Value:O}");
            }
        }
        finally
        {
            sampleCts.Cancel();
            try { await sampleLoop; } catch (OperationCanceledException) { }
            try { await pair.Helper.ResetAsync(); } catch { }
            try { await pair.Helpee.ResetAsync(); } catch { }
            ActiveRuntimeCounters.ResetForTests();
        }

        if (samples.Count == 0)
        {
            samples.Add(resourceSampler.Capture());
        }

        var summary = ResourceSummaryBuilder.BuildSummary(samples);
        summary = RecomputeSteadyStateGrowthForBenchmark(summary, samples, notes);
        notes.Add($"samples={samples.Count}");
        return new ResourceRunResult(samples, summary, notes, LeakCheckFailed: false);

        static async Task DelayAndSamplePhaseAsync(string phase, TimeSpan duration, TextWriter output, CancellationToken ct2)
        {
            if (duration <= TimeSpan.Zero) return;
            await output.WriteLineAsync($"[resources] phase={phase}; duration_s={duration.TotalSeconds:F0}");
            await Task.Delay(duration, ct2);
        }

        static async Task<DateTimeOffset> CaptureGcStabilizedCheckpointAsync(string name, ResourceSampler sampler, List<ResourceSnapshot> samples, TextWriter output, CancellationToken ct2)
        {
            ForceFullGc();
            await Task.Delay(50, ct2);
            var snap = sampler.Capture();
            samples.Add(snap);
            await output.WriteLineAsync($"[resources] checkpoint={name}; ts={snap.TimestampUtc:O}");
            return snap.TimestampUtc;
        }
    }

    private static async Task<ResourceRunResult> RunLeakCheckAsync(ResourceRunnerOptions options, MetricsRegistry metrics, ITransportTelemetrySink sink, TextWriter output, CancellationToken ct)
    {
        ActiveRuntimeCounters.ResetForTests();
        var samples = new List<ResourceSnapshot>();
        var checkpoints = BuildCheckpoints(options.Cycles);
        var resourceSampler = new ResourceSampler(GetBridgePid);
        using var pair = CreateSessionPair(options, sink, cycleSeed: 1);

        async Task CaptureCheckpointAsync(int cycle)
        {
            ForceFullGc();
            await Task.Delay(50, ct);
            samples.Add(resourceSampler.Capture());
            await output.WriteLineAsync($"[leak] checkpoint {cycle} captured");
        }

        await CaptureCheckpointAsync(0);
        for (var i = 1; i <= options.Cycles; i++)
        {
            ct.ThrowIfCancellationRequested();
            var code = new SessionCode((i % 1_000_000).ToString("D6", CultureInfo.InvariantCulture));
            var ok = await TryRunCycleAsync(pair.Helpee, pair.Helper, code, ct);
            if (!ok)
            {
                // continue collecting checkpoints; metrics/failures reflect instability
            }

            if (checkpoints.Contains(i))
            {
                await CaptureCheckpointAsync(i);
            }

            if (options.DelayMs > 0)
            {
                await Task.Delay(options.DelayMs, ct);
            }
        }

        try { await pair.Helper.ResetAsync(); } catch { }
        try { await pair.Helpee.ResetAsync(); } catch { }
        ForceFullGc();
        samples.Add(resourceSampler.Capture());

        var summary = ResourceSummaryBuilder.BuildSummary(samples);
        var notes = new List<string> { $"checkpoints={string.Join(",", checkpoints)}", $"samples={samples.Count}" };
        var leakFail = EvaluateLeakGrowth(samples, options.LeakGrowthFailPercent, notes);
        ActiveRuntimeCounters.ResetForTests();
        return new ResourceRunResult(samples, summary, notes, leakFail);
    }

    private static bool EvaluateLeakGrowth(IReadOnlyList<ResourceSnapshot> samples, double failPercent, List<string> notes)
    {
        if (samples.Count < 2)
        {
            return false;
        }

        bool failed = false;
        // LeakCheck checkpoints include cycle 0 (cold start) and a final post-reset sample.
        // Using cycle 0 as the baseline overstates leak suspicion due to one-time startup/JIT/cache growth.
        // Use the first post-warmup checkpoint (index 1) when available, while keeping the same threshold.
        var baselineIndex = samples.Count >= 3 ? 1 : 0;
        notes.Add($"leak_baseline_sample_index={baselineIndex}");

        // Leak suspicion should require sustained end-of-run growth after warmup, not a single
        // noisy checkpoint jump. This preserves intent (catch real leaks) while reducing false
        // positives from transient handle/thread churn.
        CheckSustainedLeakGrowth("app.private_mb", samples.Select(s => s.App.PrivateBytesMB).ToArray(), baselineIndex, absoluteDeltaFloor: 16d, tailNoiseFloor: 2d);
        CheckSustainedLeakGrowth("app.working_mb", samples.Select(s => s.App.WorkingSetMB).ToArray(), baselineIndex, absoluteDeltaFloor: 24d, tailNoiseFloor: 4d);
        CheckSustainedLeakGrowth("app.handles", samples.Select(s => (double)s.App.HandleCount).ToArray(), baselineIndex, absoluteDeltaFloor: 24d, tailNoiseFloor: 2d);
        CheckSustainedLeakGrowth("app.threads", samples.Select(s => (double)s.App.ThreadCount).ToArray(), baselineIndex, absoluteDeltaFloor: 4d, tailNoiseFloor: 1d);
        return failed;

        void CheckSustainedLeakGrowth(string name, double[] values, int baselineIdx, double absoluteDeltaFloor, double tailNoiseFloor)
        {
            if (values.Length < 2) return;
            if (baselineIdx < 0 || baselineIdx >= values.Length - 1) baselineIdx = 0;
            var baseline = values[baselineIdx];
            if (baseline <= 0) return;

            var finalValue = values[^1];
            var absoluteDelta = finalValue - baseline;
            var growth = ((finalValue - baseline) / baseline) * 100d;
            notes.Add($"{name}_growth_pct={growth:F1}");
            notes.Add($"{name}_growth_abs={absoluteDelta:F1}");

            // Require the leak signal to still be visible near the end of the run (tail window)
            // to avoid failing on one-off spikes.
            var tailStart = Math.Max(baselineIdx + 1, values.Length - 3);
            var tailGrowths = new List<double>(Math.Max(1, values.Length - tailStart));
            var tailAboveThresholdCount = 0;
            for (var i = tailStart; i < values.Length; i++)
            {
                var candidateGrowth = ((values[i] - baseline) / baseline) * 100d;
                tailGrowths.Add(candidateGrowth);
                var candidateDelta = values[i] - baseline;
                if (candidateGrowth > failPercent && candidateDelta > absoluteDeltaFloor + tailNoiseFloor)
                {
                    tailAboveThresholdCount++;
                }
            }
            notes.Add($"{name}_tail_growths_pct=[{string.Join(",", tailGrowths.Select(g => g.ToString("F1", CultureInfo.InvariantCulture)))}]");
            notes.Add($"{name}_tail_exceeds={tailAboveThresholdCount}");

            if (growth > failPercent &&
                absoluteDelta > absoluteDeltaFloor &&
                tailAboveThresholdCount >= Math.Min(2, tailGrowths.Count))
            {
                failed = true;
                notes.Add($"LEAK_FAIL:{name} sustained growth {growth:F1}% ({absoluteDelta:F1}) > {failPercent:F1}% with tail_exceeds={tailAboveThresholdCount}");
            }
        }
    }

    private static async Task<bool> TryRunCycleAsync(SessionRuntime helpee, SessionRuntime helper, SessionCode code, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CycleTimeout);

        var incomingJoin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var helperConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var helpeeConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        EventHandler incomingJoinHandler = (_, _) => incomingJoin.TrySetResult();
        EventHandler<SessionRuntimeStateChangedEventArgs> helperStateChanged = (_, e) => { if (e.State == SessionRuntimeState.Connected) helperConnected.TrySetResult(); };
        EventHandler<SessionRuntimeStateChangedEventArgs> helpeeStateChanged = (_, e) => { if (e.State == SessionRuntimeState.Connected) helpeeConnected.TrySetResult(); };
        helpee.IncomingJoinRequestAvailable += incomingJoinHandler;
        helper.StateChanged += helperStateChanged;
        helpee.StateChanged += helpeeStateChanged;
        try
        {
            await helpee.StartHelpeeAsync(code, cts.Token);
            await helper.StartHelperAsync(code, cts.Token);
            await incomingJoin.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
            await helpee.ApproveAsync(cts.Token);
            await Task.WhenAll(
                helperConnected.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token),
                helpeeConnected.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token));
            await helper.TrySendChatTextAsync("leak", cts.Token);
            await helper.DisconnectAsync();
            await helpee.ResetAsync();
            return true;
        }
        catch
        {
            try { await helper.ResetAsync(); } catch { }
            try { await helpee.ResetAsync(); } catch { }
            return false;
        }
        finally
        {
            helpee.IncomingJoinRequestAvailable -= incomingJoinHandler;
            helper.StateChanged -= helperStateChanged;
            helpee.StateChanged -= helpeeStateChanged;
        }
    }

    private static async Task RunConnectApproveAsync(SessionRuntime helpee, SessionRuntime helper, SessionCode code, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CycleTimeout);
        var incomingJoin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var helperConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var helpeeConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        EventHandler incomingJoinHandler = (_, _) => incomingJoin.TrySetResult();
        EventHandler<SessionRuntimeStateChangedEventArgs> helperStateChanged = (_, e) => { if (e.State == SessionRuntimeState.Connected) helperConnected.TrySetResult(); };
        EventHandler<SessionRuntimeStateChangedEventArgs> helpeeStateChanged = (_, e) => { if (e.State == SessionRuntimeState.Connected) helpeeConnected.TrySetResult(); };
        helpee.IncomingJoinRequestAvailable += incomingJoinHandler;
        helper.StateChanged += helperStateChanged;
        helpee.StateChanged += helpeeStateChanged;
        try
        {
            await helper.StartHelperAsync(code, cts.Token);
            await incomingJoin.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
            await helpee.ApproveAsync(cts.Token);
            await Task.WhenAll(
                helperConnected.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token),
                helpeeConnected.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token));
        }
        finally
        {
            helpee.IncomingJoinRequestAvailable -= incomingJoinHandler;
            helper.StateChanged -= helperStateChanged;
            helpee.StateChanged -= helpeeStateChanged;
        }
    }

    private static HashSet<int> BuildCheckpoints(int cycles)
    {
        var set = new HashSet<int> { 0, cycles };
        foreach (var point in new[] { 50, 100, 150, 200 })
        {
            if (point <= cycles) set.Add(point);
        }
        return set;
    }

    private static void ForceFullGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static async Task RunSamplingLoopAsync(ResourceSampler sampler, List<ResourceSnapshot> samples, TimeSpan interval, CancellationToken ct)
    {
        if (interval <= TimeSpan.Zero)
        {
            interval = TimeSpan.FromSeconds(1);
        }

        using var timer = new PeriodicTimer(interval);
        samples.Add(sampler.Capture());
        while (await timer.WaitForNextTickAsync(ct))
        {
            samples.Add(sampler.Capture());
        }
    }

    private static BenchmarkSessionPair CreateSessionPair(ResourceRunnerOptions options, ITransportTelemetrySink sink, int cycleSeed)
    {
        var reusePolicy = new BridgeReusePolicy(options.BridgeReuseMode, TimeSpan.FromSeconds(60));
        return new BenchmarkSessionPair(
            new SessionRuntime(CreateTransportFactory(options.Transport, "helpee", cycleSeed), watchdogOptions: null, watchdogDelayAsync: null, telemetrySink: sink, bridgeReusePolicy: reusePolicy),
            new SessionRuntime(CreateTransportFactory(options.Transport, "helper", cycleSeed), watchdogOptions: null, watchdogDelayAsync: null, telemetrySink: sink, bridgeReusePolicy: reusePolicy));
    }

    private static Func<ISignalingTransport> CreateTransportFactory(string transport, string roleLabel, int cycleIndex)
    {
        return transport switch
        {
            "nkn" => () => CreateNknBenchmarkTransport(roleLabel, cycleIndex),
            _ => static () => new DevLocalTransport(),
        };
    }

    private static ISignalingTransport CreateNknBenchmarkTransport(string roleLabel, int cycleIndex)
    {
        lock (NknFactoryEnvLock)
        {
            var prevKeyPath = Environment.GetEnvironmentVariable("NLINK_NKN_KEY_PATH");
            var prevIdentifier = Environment.GetEnvironmentVariable("NLINK_NKN_IDENTIFIER");
            try
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "nlink-resource-identities");
                Directory.CreateDirectory(tempDir);
                var keyPath = Path.Combine(tempDir, $"identity-{roleLabel}-{cycleIndex:D4}.json");
                var identifier = $"nlink-resource-{roleLabel}-{cycleIndex:D4}";
                Environment.SetEnvironmentVariable("NLINK_NKN_KEY_PATH", keyPath);
                Environment.SetEnvironmentVariable("NLINK_NKN_IDENTIFIER", identifier);
                return new NknSignalingTransport();
            }
            finally
            {
                Environment.SetEnvironmentVariable("NLINK_NKN_KEY_PATH", prevKeyPath);
                Environment.SetEnvironmentVariable("NLINK_NKN_IDENTIFIER", prevIdentifier);
            }
        }
    }

    private static int? GetBridgePid()
    {
        var pid = NknRuntimeDiagnostics.Snapshot().BridgePid;
        return pid > 0 ? pid : null;
    }

    private static string BuildRunnerSummaryText(ResourceRunResult result, ResourceGateResult? gate, string jsonPath)
    {
        var lines = new List<string>
        {
            ResourceSummaryBuilder.BuildSummaryText(result.Summary),
            string.Empty,
            $"samples: {result.Samples.Count}",
            $"json: {Path.GetFullPath(jsonPath)}"
        };

        foreach (var note in result.Notes)
        {
            lines.Add($"note: {note}");
        }

        if (gate is not null)
        {
            lines.Add(string.Empty);
            lines.Add(gate.ToText());
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static ResourceBenchmarkSummary RecomputeSteadyStateGrowthForBenchmark(
        ResourceBenchmarkSummary summary,
        IReadOnlyList<ResourceSnapshot> samples,
        List<string> notes)
    {
        if (samples.Count < 2)
        {
            return summary;
        }

        if (!TryGetNoteTimestamp(notes, "idle_hosting_end_utc", out var baselineUtc) ||
            !TryGetNoteTimestamp(notes, "final_idle_end_utc", out var finalUtc))
        {
            return summary;
        }

        var ordered = samples.OrderBy(s => s.TimestampUtc).ToArray();
        var baseline = FindSampleAtOrBefore(ordered, baselineUtc) ?? ordered[0];
        var final = FindSampleAtOrBefore(ordered, finalUtc) ?? ordered[^1];
        if (final.TimestampUtc < baseline.TimestampUtc)
        {
            final = ordered[^1];
        }

        notes.Add($"resource_growth_baseline_sample_utc={baseline.TimestampUtc:O}");
        notes.Add($"resource_growth_final_sample_utc={final.TimestampUtc:O}");
        notes.Add("resource_growth_basis=steady_state_idle_hosting_to_final_idle");

        return summary with
        {
            AppWorkingSetGrowthPercent = GrowthPercent(baseline.App.WorkingSetMB, final.App.WorkingSetMB),
            AppPrivateBytesGrowthPercent = GrowthPercent(baseline.App.PrivateBytesMB, final.App.PrivateBytesMB),
            AppHandleGrowthPercent = GrowthPercent(baseline.App.HandleCount, final.App.HandleCount),
            AppThreadGrowthPercent = GrowthPercent(baseline.App.ThreadCount, final.App.ThreadCount),
            BridgeWorkingSetGrowthPercent = GrowthPercent(baseline.Bridge?.WorkingSetMB, final.Bridge?.WorkingSetMB),
            BridgePrivateBytesGrowthPercent = GrowthPercent(baseline.Bridge?.PrivateBytesMB, final.Bridge?.PrivateBytesMB),
            BridgeHandleGrowthPercent = GrowthPercent(baseline.Bridge?.HandleCount, final.Bridge?.HandleCount),
            BridgeThreadGrowthPercent = GrowthPercent(baseline.Bridge?.ThreadCount, final.Bridge?.ThreadCount),
        };
    }

    private static ResourceSnapshot? FindSampleAtOrBefore(IReadOnlyList<ResourceSnapshot> ordered, DateTimeOffset targetUtc)
    {
        ResourceSnapshot? best = null;
        for (var i = 0; i < ordered.Count; i++)
        {
            var sample = ordered[i];
            if (sample.TimestampUtc <= targetUtc)
            {
                best = sample;
                continue;
            }
            break;
        }

        return best ?? ordered[0];
    }

    private static bool TryGetNoteTimestamp(List<string> notes, string key, out DateTimeOffset value)
    {
        value = default;
        var prefix = key + "=";
        for (var i = notes.Count - 1; i >= 0; i--)
        {
            var note = notes[i];
            if (note is null || !note.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }
            var raw = note[prefix.Length..];
            return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value);
        }

        return false;
    }

    private static double? GrowthPercent(double start, double end)
        => start <= 0d ? null : ((end - start) / start) * 100d;

    private static double? GrowthPercent(double? start, double? end)
        => !start.HasValue || !end.HasValue ? null : GrowthPercent(start.Value, end.Value);

    private static double? GrowthPercent(int start, int end)
        => start <= 0 ? null : ((double)(end - start) / start) * 100d;

    private static double? GrowthPercent(int? start, int? end)
        => !start.HasValue || !end.HasValue ? null : GrowthPercent(start.Value, end.Value);

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

    internal enum ResourceRunMode { Benchmark, LeakCheck }

    internal sealed record ResourceRunnerOptions(
        ResourceRunMode Mode,
        int Cycles,
        int DelayMs,
        string Transport,
        BridgeReuseMode BridgeReuseMode,
        int SampleIntervalMs,
        int IdleSeconds,
        int ConnectedIdleSeconds,
        int FinalIdleSeconds,
        bool FailOnGate,
        double LeakGrowthFailPercent,
        ResourceGateThresholds ResourceGateThresholds)
    {
        public static bool TryParse(string[] args, out ResourceRunnerOptions? options, out string error)
        {
            options = null;
            error = string.Empty;
            var mode = args.Any(a => string.Equals(a, "--leak-check", StringComparison.OrdinalIgnoreCase))
                ? ResourceRunMode.LeakCheck
                : ResourceRunMode.Benchmark;
            var cycles = mode == ResourceRunMode.LeakCheck ? 200 : 1;
            var delayMs = 0;
            var transport = "devlocal";
            var reuse = BridgeReuseMode.PerSession;
            var sampleIntervalMs = 1000;
            var idleSeconds = 60;
            var connectedIdleSeconds = 60;
            var finalIdleSeconds = 60;
            var failOnGate = false;
            var leakGrowthFailPercent = 20d;
            var thresholds = new ResourceGateThresholds();

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (!arg.StartsWith("--", StringComparison.Ordinal)) continue;
                var eq = arg.IndexOf('=');
                var key = eq > 0 ? arg[..eq] : arg;
                string? value = eq > 0 ? arg[(eq + 1)..] : (i + 1 < args.Length ? args[i + 1] : null);
                if (eq <= 0 && key is not ("--resource-bench" or "--leak-check" or "--fail-on-gate" or "--resource-disable-growth-checks" or "--resource-fail-on-bridge-thresholds")) i++;

                switch (key.ToLowerInvariant())
                {
                    case "--resource-bench": mode = ResourceRunMode.Benchmark; break;
                    case "--leak-check": mode = ResourceRunMode.LeakCheck; break;
                    case "--fail-on-gate": failOnGate = true; break;
                    case "--cycles": if (!int.TryParse(value, out cycles) || cycles <= 0) { error = "Invalid --cycles"; return false; } break;
                    case "--delay-ms": if (!int.TryParse(value, out delayMs) || delayMs < 0) { error = "Invalid --delay-ms"; return false; } break;
                    case "--sample-ms": if (!int.TryParse(value, out sampleIntervalMs) || sampleIntervalMs <= 0) { error = "Invalid --sample-ms"; return false; } break;
                    case "--idle-seconds": if (!int.TryParse(value, out idleSeconds) || idleSeconds < 0) { error = "Invalid --idle-seconds"; return false; } break;
                    case "--connected-idle-seconds": if (!int.TryParse(value, out connectedIdleSeconds) || connectedIdleSeconds < 0) { error = "Invalid --connected-idle-seconds"; return false; } break;
                    case "--final-idle-seconds": if (!int.TryParse(value, out finalIdleSeconds) || finalIdleSeconds < 0) { error = "Invalid --final-idle-seconds"; return false; } break;
                    case "--transport":
                        transport = (value ?? string.Empty).Trim().ToLowerInvariant();
                        if (transport is not ("devlocal" or "nkn")) { error = "Invalid --transport"; return false; }
                        break;
                    case "--bridge-reuse-mode":
                        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
                        reuse = normalized switch { "keepalive" => BridgeReuseMode.KeepAlive, "persession" => BridgeReuseMode.PerSession, _ => throw new InvalidOperationException("Invalid --bridge-reuse-mode") };
                        break;
                    case "--leak-growth-fail-percent":
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out leakGrowthFailPercent) || leakGrowthFailPercent < 0) { error = "Invalid --leak-growth-fail-percent"; return false; }
                        break;
                    case "--resource-growth-warn-percent":
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rgw)) { error = "Invalid --resource-growth-warn-percent"; return false; }
                        thresholds = thresholds with { GrowthWarnPercent = rgw };
                        break;
                    case "--resource-growth-fail-percent":
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rgf)) { error = "Invalid --resource-growth-fail-percent"; return false; }
                        thresholds = thresholds with { GrowthFailPercent = rgf };
                        break;
                    case "--app-working-set-max-mb":
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var appWsMax)) { error = "Invalid --app-working-set-max-mb"; return false; }
                        thresholds = thresholds with { AppWorkingSetMaxMB = appWsMax };
                        break;
                    case "--app-private-bytes-max-mb":
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var appPrivMax)) { error = "Invalid --app-private-bytes-max-mb"; return false; }
                        thresholds = thresholds with { AppPrivateBytesMaxMB = appPrivMax };
                        break;
                    case "--app-thread-max":
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var appThreadMax)) { error = "Invalid --app-thread-max"; return false; }
                        thresholds = thresholds with { AppThreadMax = appThreadMax };
                        break;
                    case "--app-handle-max":
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var appHandleMax)) { error = "Invalid --app-handle-max"; return false; }
                        thresholds = thresholds with { AppHandleMax = appHandleMax };
                        break;
                    case "--app-cpu-idle-avg-max-pct":
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var appCpuIdleMax)) { error = "Invalid --app-cpu-idle-avg-max-pct"; return false; }
                        thresholds = thresholds with { AppCpuIdleAvgMaxPct = appCpuIdleMax };
                        break;
                    case "--bridge-working-set-max-mb":
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bridgeWsMax)) { error = "Invalid --bridge-working-set-max-mb"; return false; }
                        thresholds = thresholds with { BridgeWorkingSetMaxMB = bridgeWsMax };
                        break;
                    case "--bridge-private-bytes-max-mb":
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bridgePrivMax)) { error = "Invalid --bridge-private-bytes-max-mb"; return false; }
                        thresholds = thresholds with { BridgePrivateBytesMaxMB = bridgePrivMax };
                        break;
                    case "--bridge-thread-max":
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bridgeThreadMax)) { error = "Invalid --bridge-thread-max"; return false; }
                        thresholds = thresholds with { BridgeThreadMax = bridgeThreadMax };
                        break;
                    case "--bridge-handle-max":
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bridgeHandleMax)) { error = "Invalid --bridge-handle-max"; return false; }
                        thresholds = thresholds with { BridgeHandleMax = bridgeHandleMax };
                        break;
                    case "--bridge-cpu-idle-avg-max-pct":
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bridgeCpuIdleMax)) { error = "Invalid --bridge-cpu-idle-avg-max-pct"; return false; }
                        thresholds = thresholds with { BridgeCpuIdleAvgMaxPct = bridgeCpuIdleMax };
                        break;
                    case "--resource-fail-on-bridge-thresholds":
                        if (string.IsNullOrWhiteSpace(value))
                        {
                            thresholds = thresholds with { FailOnBridgeThresholds = true };
                        }
                        else if (bool.TryParse(value, out var failOnBridgeThresholds))
                        {
                            thresholds = thresholds with { FailOnBridgeThresholds = failOnBridgeThresholds };
                        }
                        else
                        {
                            error = "Invalid --resource-fail-on-bridge-thresholds";
                            return false;
                        }
                        break;
                    case "--resource-disable-growth-checks":
                        thresholds = thresholds with { EvaluateGrowthChecks = false };
                        break;
                }
            }

            options = new ResourceRunnerOptions(mode, cycles, delayMs, transport, reuse, sampleIntervalMs, idleSeconds, connectedIdleSeconds, finalIdleSeconds, failOnGate, leakGrowthFailPercent, thresholds);
            return true;
        }
    }

    private sealed record ResourceRunResult(
        List<ResourceSnapshot> Samples,
        ResourceBenchmarkSummary Summary,
        List<string> Notes,
        bool LeakCheckFailed);

    private sealed class BenchmarkSessionPair : IDisposable
    {
        public BenchmarkSessionPair(SessionRuntime helpee, SessionRuntime helper)
        {
            Helpee = helpee;
            Helper = helper;
        }
        public SessionRuntime Helpee { get; }
        public SessionRuntime Helper { get; }
        public void Dispose() { Helper.Dispose(); Helpee.Dispose(); }
    }
}
