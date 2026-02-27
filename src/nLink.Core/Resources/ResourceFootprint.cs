using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NLink.Core.Resources;

public sealed record ResourceProcessSnapshot(
    int Pid,
    string ProcessName,
    double WorkingSetMB,
    double PrivateBytesMB,
    double GCHeapMB,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    int ThreadCount,
    int HandleCount,
    double CpuPercent);

public sealed record ResourceSnapshot(
    DateTimeOffset TimestampUtc,
    ResourceProcessSnapshot App,
    ResourceProcessSnapshot? Bridge,
    ActiveResourceCountersSnapshot ActiveCounters)
{
    public string ToJson(bool indented = true)
        => JsonSerializer.Serialize(this, ResourceJson.Options(indented));
}

public sealed record ActiveResourceCountersSnapshot(
    long ActiveSessions,
    long ActiveConnectAttempts,
    long ActiveRetryTimers,
    long ActiveWatchdogs,
    long ActiveTransportTasks,
    long ActiveBridgeIoReaders);

public static class ActiveRuntimeCounters
{
    private static long activeSessions;
    private static long activeConnectAttempts;
    private static long maxActiveConnectAttempts;
    private static long activeRetryTimers;
    private static long activeWatchdogs;
    private static long activeTransportTasks;
    private static long activeBridgeIoReaders;

    public static void IncSessions() => Interlocked.Increment(ref activeSessions);
    public static void DecSessions() => DecrementNonNegative(ref activeSessions);
    public static void IncConnectAttempts()
    {
        var next = Interlocked.Increment(ref activeConnectAttempts);
        UpdateMax(ref maxActiveConnectAttempts, next);
    }
    public static void DecConnectAttempts() => DecrementNonNegative(ref activeConnectAttempts);
    public static void IncRetryTimers() => Interlocked.Increment(ref activeRetryTimers);
    public static void DecRetryTimers() => DecrementNonNegative(ref activeRetryTimers);
    public static void IncWatchdogs() => Interlocked.Increment(ref activeWatchdogs);
    public static void DecWatchdogs() => DecrementNonNegative(ref activeWatchdogs);
    public static void IncTransportTasks() => Interlocked.Increment(ref activeTransportTasks);
    public static void DecTransportTasks() => DecrementNonNegative(ref activeTransportTasks);
    public static void IncBridgeIoReaders() => Interlocked.Increment(ref activeBridgeIoReaders);
    public static void DecBridgeIoReaders() => DecrementNonNegative(ref activeBridgeIoReaders);

    public static ActiveResourceCountersSnapshot Snapshot() => new(
        Interlocked.Read(ref activeSessions),
        Interlocked.Read(ref activeConnectAttempts),
        Interlocked.Read(ref activeRetryTimers),
        Interlocked.Read(ref activeWatchdogs),
        Interlocked.Read(ref activeTransportTasks),
        Interlocked.Read(ref activeBridgeIoReaders));

    public static long MaxActiveConnectAttemptsObserved()
        => Interlocked.Read(ref maxActiveConnectAttempts);

    public static void ResetForTests()
    {
        Interlocked.Exchange(ref activeSessions, 0);
        Interlocked.Exchange(ref activeConnectAttempts, 0);
        Interlocked.Exchange(ref maxActiveConnectAttempts, 0);
        Interlocked.Exchange(ref activeRetryTimers, 0);
        Interlocked.Exchange(ref activeWatchdogs, 0);
        Interlocked.Exchange(ref activeTransportTasks, 0);
        Interlocked.Exchange(ref activeBridgeIoReaders, 0);
    }

    private static void DecrementNonNegative(ref long target)
    {
        while (true)
        {
            var current = Interlocked.Read(ref target);
            if (current <= 0)
            {
                return;
            }
            if (Interlocked.CompareExchange(ref target, current - 1, current) == current)
            {
                return;
            }
        }
    }

    private static void UpdateMax(ref long target, long candidate)
    {
        while (true)
        {
            var current = Interlocked.Read(ref target);
            if (candidate <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref target, candidate, current) == current)
            {
                return;
            }
        }
    }
}

public sealed class ResourceSampler
{
    private readonly Func<int?> bridgePidProvider;
    private readonly int processorCount;
    private readonly object gate = new();
    private readonly Dictionary<string, CpuSampleState> cpuStates = new(StringComparer.Ordinal);

    public ResourceSampler(Func<int?>? bridgePidProvider = null, int? processorCount = null)
    {
        this.bridgePidProvider = bridgePidProvider ?? (() => null);
        this.processorCount = Math.Max(1, processorCount ?? Environment.ProcessorCount);
    }

    public ResourceSnapshot Capture()
    {
        var now = DateTimeOffset.UtcNow;
        using var appProcess = Process.GetCurrentProcess();
        var app = CaptureProcess(appProcess, now, includeGc: true);

        ResourceProcessSnapshot? bridge = null;
        var bridgePid = bridgePidProvider();
        if (bridgePid is > 0)
        {
            bridge = TryCaptureBridgeProcess(bridgePid.Value, now);
        }

        return new ResourceSnapshot(now, app, bridge, ActiveRuntimeCounters.Snapshot());
    }

    private ResourceProcessSnapshot? TryCaptureBridgeProcess(int pid, DateTimeOffset now)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return null;
            }

            return CaptureProcess(process, now, includeGc: false);
        }
        catch
        {
            return null;
        }
    }

    private ResourceProcessSnapshot CaptureProcess(Process process, DateTimeOffset now, bool includeGc)
    {
        try { process.Refresh(); } catch { }

        var pid = SafeGetPid(process);
        var processName = SafeGetProcessName(process);
        var workingSet = SafeGet(() => process.WorkingSet64);
        var privateBytes = SafeGet(() => process.PrivateMemorySize64);
        var threadCount = (int)Math.Clamp(SafeGet(() => process.Threads.Count), 0, int.MaxValue);
        var handleCount = (int)Math.Clamp(SafeGet(() => process.HandleCount), 0, int.MaxValue);
        var cpu = CaptureCpuPercent(process, now, pid);

        return new ResourceProcessSnapshot(
            Pid: pid,
            ProcessName: processName,
            WorkingSetMB: BytesToMb(workingSet),
            PrivateBytesMB: BytesToMb(privateBytes),
            GCHeapMB: includeGc ? BytesToMb(GC.GetTotalMemory(false)) : 0d,
            Gen0Collections: includeGc ? GC.CollectionCount(0) : 0,
            Gen1Collections: includeGc ? GC.CollectionCount(1) : 0,
            Gen2Collections: includeGc ? GC.CollectionCount(2) : 0,
            ThreadCount: threadCount,
            HandleCount: handleCount,
            CpuPercent: cpu);
    }

    private double CaptureCpuPercent(Process process, DateTimeOffset now, int pid)
    {
        if (pid <= 0)
        {
            return 0;
        }

        TimeSpan totalCpu;
        try { totalCpu = process.TotalProcessorTime; } catch { return 0; }

        var key = pid.ToString(CultureInfo.InvariantCulture);
        lock (gate)
        {
            if (!cpuStates.TryGetValue(key, out var previous))
            {
                cpuStates[key] = new CpuSampleState(now, totalCpu);
                return 0;
            }

            var elapsedWallMs = (now - previous.TimestampUtc).TotalMilliseconds;
            var deltaCpuMs = (totalCpu - previous.TotalProcessorTime).TotalMilliseconds;
            cpuStates[key] = new CpuSampleState(now, totalCpu);
            return CpuUsageCalculator.CalculatePercent(deltaCpuMs, elapsedWallMs, processorCount);
        }
    }

    private static long SafeGet(Func<int> getter)
    {
        try { return getter(); } catch { return 0; }
    }

    private static long SafeGet(Func<long> getter)
    {
        try { return getter(); } catch { return 0; }
    }

    private static int SafeGetPid(Process p) { try { return p.Id; } catch { return 0; } }
    private static string SafeGetProcessName(Process p) { try { return p.ProcessName; } catch { return string.Empty; } }
    private static double BytesToMb(long bytes) => bytes <= 0 ? 0d : bytes / (1024d * 1024d);

    private readonly record struct CpuSampleState(DateTimeOffset TimestampUtc, TimeSpan TotalProcessorTime);
}

public static class CpuUsageCalculator
{
    public static double CalculatePercent(double deltaCpuMs, double elapsedWallMs, int processorCount)
    {
        if (deltaCpuMs <= 0 || elapsedWallMs <= 0 || processorCount <= 0)
        {
            return 0;
        }

        var pct = (deltaCpuMs / (elapsedWallMs * processorCount)) * 100d;
        if (double.IsNaN(pct) || double.IsInfinity(pct))
        {
            return 0;
        }

        return Math.Max(0d, pct);
    }
}

public static class ResourceJson
{
    public static JsonSerializerOptions Options(bool indented)
        => new()
        {
            WriteIndented = indented,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
}

public sealed record ResourceSeriesSummary(double Min, double Avg, double Max, double Peak, double P50, double P95, int Count)
{
    public static ResourceSeriesSummary? From(IEnumerable<double> values)
    {
        var arr = values.Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).OrderBy(v => v).ToArray();
        if (arr.Length == 0) return null;
        return new ResourceSeriesSummary(
            Min: arr[0],
            Avg: arr.Average(),
            Max: arr[^1],
            Peak: arr[^1],
            P50: Percentile(arr, 50),
            P95: Percentile(arr, 95),
            Count: arr.Length);
    }

    private static double Percentile(double[] sorted, double p)
    {
        var rank = (p / 100d) * (sorted.Length - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        if (lo == hi) return sorted[lo];
        var frac = rank - lo;
        return sorted[lo] + ((sorted[hi] - sorted[lo]) * frac);
    }
}

public sealed record ResourceProcessSummary(
    ResourceSeriesSummary? WorkingSetMB,
    ResourceSeriesSummary? PrivateBytesMB,
    ResourceSeriesSummary? CpuPercent,
    ResourceSeriesSummary? ThreadCount,
    ResourceSeriesSummary? HandleCount,
    ResourceSeriesSummary? GCHeapMB);

public sealed record ResourceBenchmarkSummary(
    ResourceSnapshot? LastSnapshot,
    ResourceSnapshot? PeakSnapshot,
    ResourceProcessSummary? App,
    ResourceProcessSummary? Bridge,
    double? AppWorkingSetGrowthPercent,
    double? AppPrivateBytesGrowthPercent,
    double? AppHandleGrowthPercent,
    double? AppThreadGrowthPercent,
    double? BridgeWorkingSetGrowthPercent,
    double? BridgePrivateBytesGrowthPercent,
    double? BridgeHandleGrowthPercent,
    double? BridgeThreadGrowthPercent,
    ActiveResourceCountersSnapshot FinalActiveCounters);

public sealed record ResourceRunMetadata(
    string Version,
    string Transport,
    string BridgeReuseMode,
    int Cycles,
    string Scenario,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc);

public sealed record ResourceBenchmarkArtifact(
    ResourceRunMetadata Metadata,
    ResourceSnapshot[] Samples,
    ResourceBenchmarkSummary Summary,
    ResourceGateResult? ResourceGate,
    string[] Notes);

public static class ResourceSummaryBuilder
{
    public static ResourceBenchmarkSummary BuildSummary(IReadOnlyList<ResourceSnapshot> samples)
    {
        var arr = samples?.OrderBy(s => s.TimestampUtc).ToArray() ?? Array.Empty<ResourceSnapshot>();
        if (arr.Length == 0)
        {
            return new ResourceBenchmarkSummary(
                LastSnapshot: null,
                PeakSnapshot: null,
                App: null,
                Bridge: null,
                AppWorkingSetGrowthPercent: null,
                AppPrivateBytesGrowthPercent: null,
                AppHandleGrowthPercent: null,
                AppThreadGrowthPercent: null,
                BridgeWorkingSetGrowthPercent: null,
                BridgePrivateBytesGrowthPercent: null,
                BridgeHandleGrowthPercent: null,
                BridgeThreadGrowthPercent: null,
                FinalActiveCounters: ActiveRuntimeCounters.Snapshot());
        }

        var appSummary = BuildProcessSummary(arr.Select(s => s.App));
        var bridgeSamples = arr.Where(s => s.Bridge is not null).Select(s => s.Bridge!).ToArray();
        var bridgeSummary = bridgeSamples.Length > 0 ? BuildProcessSummary(bridgeSamples) : null;
        var last = arr[^1];
        var peak = FindPeakSnapshot(arr);

        var baseline = arr[0];
        return new ResourceBenchmarkSummary(
            LastSnapshot: last,
            PeakSnapshot: peak,
            App: appSummary,
            Bridge: bridgeSummary,
            AppWorkingSetGrowthPercent: GrowthPercent(baseline.App.WorkingSetMB, last.App.WorkingSetMB),
            AppPrivateBytesGrowthPercent: GrowthPercent(baseline.App.PrivateBytesMB, last.App.PrivateBytesMB),
            AppHandleGrowthPercent: GrowthPercent(baseline.App.HandleCount, last.App.HandleCount),
            AppThreadGrowthPercent: GrowthPercent(baseline.App.ThreadCount, last.App.ThreadCount),
            BridgeWorkingSetGrowthPercent: GrowthPercent(baseline.Bridge?.WorkingSetMB, last.Bridge?.WorkingSetMB),
            BridgePrivateBytesGrowthPercent: GrowthPercent(baseline.Bridge?.PrivateBytesMB, last.Bridge?.PrivateBytesMB),
            BridgeHandleGrowthPercent: GrowthPercent(baseline.Bridge?.HandleCount, last.Bridge?.HandleCount),
            BridgeThreadGrowthPercent: GrowthPercent(baseline.Bridge?.ThreadCount, last.Bridge?.ThreadCount),
            FinalActiveCounters: last.ActiveCounters);
    }

    public static string BuildSummaryText(ResourceBenchmarkSummary summary)
    {
        var lines = new List<string>
        {
            "Resource Summary",
            "----------------",
        };

        AppendProcessSummary(lines, "App", summary.App);
        AppendProcessSummary(lines, "Bridge", summary.Bridge);
        AppendGrowth(lines, "app_working_set_growth_pct", summary.AppWorkingSetGrowthPercent);
        AppendGrowth(lines, "app_private_bytes_growth_pct", summary.AppPrivateBytesGrowthPercent);
        AppendGrowth(lines, "app_handle_growth_pct", summary.AppHandleGrowthPercent);
        AppendGrowth(lines, "app_thread_growth_pct", summary.AppThreadGrowthPercent);
        AppendGrowth(lines, "bridge_working_set_growth_pct", summary.BridgeWorkingSetGrowthPercent);
        AppendGrowth(lines, "bridge_private_bytes_growth_pct", summary.BridgePrivateBytesGrowthPercent);
        AppendGrowth(lines, "bridge_handle_growth_pct", summary.BridgeHandleGrowthPercent);
        AppendGrowth(lines, "bridge_thread_growth_pct", summary.BridgeThreadGrowthPercent);
        lines.Add($"active_sessions: {summary.FinalActiveCounters.ActiveSessions}");
        lines.Add($"active_connect_attempts: {summary.FinalActiveCounters.ActiveConnectAttempts}");
        lines.Add($"active_retry_timers: {summary.FinalActiveCounters.ActiveRetryTimers}");
        lines.Add($"active_watchdogs: {summary.FinalActiveCounters.ActiveWatchdogs}");
        lines.Add($"active_transport_tasks: {summary.FinalActiveCounters.ActiveTransportTasks}");
        lines.Add($"active_bridge_io_readers: {summary.FinalActiveCounters.ActiveBridgeIoReaders}");
        return string.Join(Environment.NewLine, lines);
    }

    private static ResourceBenchmarkSummary BuildCheckpointSummary(IReadOnlyList<ResourceSnapshot> samples, int _dummy = 0)
        => BuildSummary(samples);

    private static ResourceProcessSummary BuildProcessSummary(IEnumerable<ResourceProcessSnapshot> samples)
    {
        var arr = samples.ToArray();
        return new ResourceProcessSummary(
            ResourceSeriesSummary.From(arr.Select(s => s.WorkingSetMB)),
            ResourceSeriesSummary.From(arr.Select(s => s.PrivateBytesMB)),
            ResourceSeriesSummary.From(arr.Select(s => s.CpuPercent)),
            ResourceSeriesSummary.From(arr.Select(s => (double)s.ThreadCount)),
            ResourceSeriesSummary.From(arr.Select(s => (double)s.HandleCount)),
            ResourceSeriesSummary.From(arr.Select(s => s.GCHeapMB)));
    }

    private static ResourceSnapshot FindPeakSnapshot(ResourceSnapshot[] samples)
    {
        ResourceSnapshot best = samples[0];
        var bestScore = Score(samples[0]);
        for (var i = 1; i < samples.Length; i++)
        {
            var score = Score(samples[i]);
            if (score > bestScore)
            {
                best = samples[i];
                bestScore = score;
            }
        }

        return best;
    }

    private static double Score(ResourceSnapshot s)
    {
        var bridgeWs = s.Bridge?.WorkingSetMB ?? 0d;
        return s.App.WorkingSetMB + bridgeWs;
    }

    private static double? GrowthPercent(double start, double end)
    {
        if (start <= 0d) return null;
        return ((end - start) / start) * 100d;
    }

    private static double? GrowthPercent(double? start, double? end)
    {
        if (!start.HasValue || !end.HasValue) return null;
        return GrowthPercent(start.Value, end.Value);
    }

    private static double? GrowthPercent(int start, int end)
        => start <= 0 ? null : ((double)(end - start) / start) * 100d;

    private static double? GrowthPercent(int? start, int? end)
    {
        if (!start.HasValue || !end.HasValue) return null;
        return GrowthPercent(start.Value, end.Value);
    }

    private static void AppendProcessSummary(List<string> lines, string title, ResourceProcessSummary? summary)
    {
        lines.Add(string.Empty);
        lines.Add(title);
        lines.Add(new string('-', title.Length));
        if (summary is null)
        {
            lines.Add("(none)");
            return;
        }

        AppendSeries(lines, "working_set_mb", summary.WorkingSetMB);
        AppendSeries(lines, "private_bytes_mb", summary.PrivateBytesMB);
        AppendSeries(lines, "cpu_pct", summary.CpuPercent);
        AppendSeries(lines, "threads", summary.ThreadCount);
        AppendSeries(lines, "handles", summary.HandleCount);
        if (title == "App")
        {
            AppendSeries(lines, "gc_heap_mb", summary.GCHeapMB);
        }
    }

    private static void AppendSeries(List<string> lines, string name, ResourceSeriesSummary? series)
    {
        if (series is null)
        {
            lines.Add($"{name}: (none)");
            return;
        }

        lines.Add($"{name}: count={series.Count}, min={series.Min:F2}, avg={series.Avg:F2}, max={series.Max:F2}, p50={series.P50:F2}, p95={series.P95:F2}");
    }

    private static void AppendGrowth(List<string> lines, string name, double? value)
    {
        lines.Add($"{name}: {(value.HasValue ? value.Value.ToString("F1", CultureInfo.InvariantCulture) : "(none)")}");
    }
}
