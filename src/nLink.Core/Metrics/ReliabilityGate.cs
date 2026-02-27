using System.Text.Json;

namespace NLink.Core.Metrics;

public sealed record ReliabilityGateThresholds(
    double MinSuccessRatePercent = 100d,
    bool RequireNoUnknownFailures = true,
    bool RequireNoStuckStates = true,
    bool FailOnBridgeCrash = true);

public sealed record ReliabilityGateInput(
    MetricsSnapshot Metrics,
    double? SuccessRatePercent = null,
    string Transport = "",
    string Scenario = "",
    string BridgeReuseMode = "");

public sealed record ReliabilityGateFailure(
    string Code,
    string Message);

public sealed record ReliabilityGateResult(
    bool Passed,
    ReliabilityGateThresholds Thresholds,
    double? SuccessRatePercent,
    long UnknownFailures,
    long StateStuckCount,
    long BridgeCrashTotal,
    ReliabilityGateFailure[] Failures)
{
    public string ToJson(bool indented = false)
    {
        return JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = indented });
    }
}

public static class ReliabilityGate
{
    public static ReliabilityGateResult Evaluate(ReliabilityGateInput input, ReliabilityGateThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(thresholds);

        var failures = new List<ReliabilityGateFailure>(4);
        var metrics = input.Metrics ?? throw new ArgumentNullException(nameof(input.Metrics));

        var unknownFailures = SumCounter(metrics, "transport_failure_total", input, failureCategory: "Unknown");
        var stateStuckCount = SumCounter(metrics, "state_stuck_count", input);
        var bridgeCrashTotal = SumCounter(metrics, "bridge_crash_total", input);

        var successRate = input.SuccessRatePercent;
        if (successRate.HasValue && successRate.Value < thresholds.MinSuccessRatePercent)
        {
            failures.Add(new ReliabilityGateFailure(
                "success_rate_below_target",
                $"success_rate={successRate.Value:F1}% is below target {thresholds.MinSuccessRatePercent:F1}%"));
        }

        if (thresholds.RequireNoUnknownFailures && unknownFailures > 0)
        {
            failures.Add(new ReliabilityGateFailure(
                "unknown_failures_present",
                $"Unknown failures > 0 ({unknownFailures})."));
        }

        if (thresholds.RequireNoStuckStates && stateStuckCount > 0)
        {
            failures.Add(new ReliabilityGateFailure(
                "state_stuck_detected",
                $"state_stuck_count > 0 ({stateStuckCount})."));
        }

        if (thresholds.FailOnBridgeCrash && bridgeCrashTotal > 0)
        {
            failures.Add(new ReliabilityGateFailure(
                "bridge_crash_detected",
                $"bridge_crash_total > 0 ({bridgeCrashTotal})."));
        }

        return new ReliabilityGateResult(
            Passed: failures.Count == 0,
            Thresholds: thresholds,
            SuccessRatePercent: successRate,
            UnknownFailures: unknownFailures,
            StateStuckCount: stateStuckCount,
            BridgeCrashTotal: bridgeCrashTotal,
            Failures: failures.ToArray());
    }

    private static long SumCounter(
        MetricsSnapshot snapshot,
        string metricName,
        ReliabilityGateInput input,
        string? failureCategory = null)
    {
        IEnumerable<CounterMetricSnapshot> query = snapshot.Counters.Where(c => string.Equals(c.Name, metricName, StringComparison.Ordinal));

        if (!string.IsNullOrWhiteSpace(input.Transport))
        {
            query = query.Where(c => string.Equals(c.Tags.Transport, input.Transport, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(input.BridgeReuseMode))
        {
            query = query.Where(c => string.Equals(c.Tags.BridgeReuseMode, input.BridgeReuseMode, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(input.Scenario))
        {
            query = query.Where(c => string.Equals(c.Tags.Scenario, input.Scenario, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(failureCategory))
        {
            query = query.Where(c => string.Equals(c.Tags.FailureCategory, failureCategory, StringComparison.OrdinalIgnoreCase));
        }

        return query.Sum(c => c.Value);
    }
}
