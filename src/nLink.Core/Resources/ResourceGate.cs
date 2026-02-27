namespace NLink.Core.Resources;

public sealed record ResourceGateThresholds(
    double AppWorkingSetMaxMB = 1024,
    double AppPrivateBytesMaxMB = 1024,
    double AppThreadMax = 400,
    double AppHandleMax = 20000,
    double AppCpuIdleAvgMaxPct = 40,
    double BridgeWorkingSetMaxMB = 512,
    double BridgePrivateBytesMaxMB = 512,
    double BridgeThreadMax = 300,
    double BridgeHandleMax = 20000,
    double BridgeCpuIdleAvgMaxPct = 40,
    double GrowthWarnPercent = 10,
    double GrowthFailPercent = 20,
    bool FailOnBridgeThresholds = false,
    bool EvaluateGrowthChecks = true);

public sealed record ResourceGateInput(
    ResourceBenchmarkSummary Summary,
    string Transport,
    string BridgeReuseMode,
    string Scenario);

public sealed record ResourceGateFailure(string Code, string Message);

public sealed record ResourceGateResult(
    bool Passed,
    ResourceGateThresholds Thresholds,
    ResourceGateFailure[] Failures,
    string[] Warnings)
{
    public string ToText()
    {
        var lines = new List<string>
        {
            $"Resource gate: {(Passed ? "PASS" : "FAIL")}"
        };
        foreach (var failure in Failures)
        {
            lines.Add($"FAIL [{failure.Code}] {failure.Message}");
        }
        foreach (var warning in Warnings)
        {
            lines.Add($"WARN {warning}");
        }
        return string.Join(Environment.NewLine, lines);
    }
}

public static class ResourceGate
{
    public static ResourceGateResult Evaluate(ResourceGateInput input, ResourceGateThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(thresholds);
        var failures = new List<ResourceGateFailure>();
        var warnings = new List<string>();
        var summary = input.Summary;
        var app = summary.App;
        var bridge = summary.Bridge;

        CheckSeries(app?.WorkingSetMB?.Peak, thresholds.AppWorkingSetMaxMB, "app_working_set_mb", failures);
        CheckSeries(app?.PrivateBytesMB?.Peak, thresholds.AppPrivateBytesMaxMB, "app_private_bytes_mb", failures);
        CheckSeries(app?.ThreadCount?.Peak, thresholds.AppThreadMax, "app_thread_count", failures);
        CheckSeries(app?.HandleCount?.Peak, thresholds.AppHandleMax, "app_handle_count", failures);
        CheckSeries(app?.CpuPercent?.Avg, thresholds.AppCpuIdleAvgMaxPct, "app_cpu_idle_avg_pct", failures);

        if (thresholds.FailOnBridgeThresholds)
        {
            CheckSeries(bridge?.WorkingSetMB?.Peak, thresholds.BridgeWorkingSetMaxMB, "bridge_working_set_mb", failures);
            CheckSeries(bridge?.PrivateBytesMB?.Peak, thresholds.BridgePrivateBytesMaxMB, "bridge_private_bytes_mb", failures);
            CheckSeries(bridge?.ThreadCount?.Peak, thresholds.BridgeThreadMax, "bridge_thread_count", failures);
            CheckSeries(bridge?.HandleCount?.Peak, thresholds.BridgeHandleMax, "bridge_handle_count", failures);
            CheckSeries(bridge?.CpuPercent?.Avg, thresholds.BridgeCpuIdleAvgMaxPct, "bridge_cpu_idle_avg_pct", failures);
        }

        if (thresholds.EvaluateGrowthChecks)
        {
            CheckGrowth(summary.AppWorkingSetGrowthPercent, "app_working_set_growth_pct", thresholds, failures, warnings);
            CheckGrowth(summary.AppPrivateBytesGrowthPercent, "app_private_growth_pct", thresholds, failures, warnings);
            CheckGrowth(summary.AppHandleGrowthPercent, "app_handle_growth_pct", thresholds, failures, warnings);
            CheckGrowth(summary.AppThreadGrowthPercent, "app_thread_growth_pct", thresholds, failures, warnings);
            CheckGrowth(summary.BridgeWorkingSetGrowthPercent, "bridge_working_set_growth_pct", thresholds, failures, warnings);
            CheckGrowth(summary.BridgePrivateBytesGrowthPercent, "bridge_private_growth_pct", thresholds, failures, warnings);
            CheckGrowth(summary.BridgeHandleGrowthPercent, "bridge_handle_growth_pct", thresholds, failures, warnings);
            CheckGrowth(summary.BridgeThreadGrowthPercent, "bridge_thread_growth_pct", thresholds, failures, warnings);
        }

        var active = summary.FinalActiveCounters;
        if (active.ActiveSessions != 0 || active.ActiveConnectAttempts != 0 || active.ActiveRetryTimers != 0 || active.ActiveWatchdogs != 0 || active.ActiveTransportTasks != 0 || active.ActiveBridgeIoReaders != 0)
        {
            failures.Add(new ResourceGateFailure(
                "active_counters_not_zero",
                $"Active counters not zero after cleanup: sessions={active.ActiveSessions}, connects={active.ActiveConnectAttempts}, retry_timers={active.ActiveRetryTimers}, watchdogs={active.ActiveWatchdogs}, transport_tasks={active.ActiveTransportTasks}, bridge_io_readers={active.ActiveBridgeIoReaders}."));
        }

        return new ResourceGateResult(failures.Count == 0, thresholds, failures.ToArray(), warnings.ToArray());
    }

    private static void CheckSeries(double? observed, double threshold, string code, List<ResourceGateFailure> failures)
    {
        if (!observed.HasValue) return;
        if (observed.Value > threshold)
        {
            failures.Add(new ResourceGateFailure(code, $"{code} observed {observed.Value:F2} > threshold {threshold:F2}."));
        }
    }

    private static void CheckGrowth(double? growthPct, string code, ResourceGateThresholds thresholds, List<ResourceGateFailure> failures, List<string> warnings)
    {
        if (!growthPct.HasValue) return;
        if (growthPct.Value > thresholds.GrowthFailPercent)
        {
            failures.Add(new ResourceGateFailure(code, $"{code} growth {growthPct.Value:F1}% > fail threshold {thresholds.GrowthFailPercent:F1}%."));
            return;
        }
        if (growthPct.Value > thresholds.GrowthWarnPercent)
        {
            warnings.Add($"{code} growth {growthPct.Value:F1}% > warn threshold {thresholds.GrowthWarnPercent:F1}%.");
        }
    }
}
